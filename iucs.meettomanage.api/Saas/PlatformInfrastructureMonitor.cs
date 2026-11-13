using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Common.Options;
using iucs.meettomanage.application.Dto.Monitoring;

namespace iucs.meettomanage.api.Saas;

/// <summary>Tenant-independent Prometheus reader for platform login, status and infrastructure pages.</summary>
public sealed class PlatformInfrastructureMonitor(IPrometheusClient prometheus)
{
    public async Task<PlatformInfrastructureDto> GetAsync(string baseUrl, IReadOnlyCollection<MonitoredServerOptions> servers, CancellationToken ct)
    {
        var serverTasks = servers.Select(x => ServerAsync(baseUrl, x, ct)).ToList();
        var alertsTask = prometheus.GetActiveAlertsAsync(baseUrl, ct);
        await Task.WhenAll(serverTasks.Cast<Task>().Append(alertsTask));
        return new PlatformInfrastructureDto
        {
            Servers = serverTasks.Select(x => x.Result).ToList(),
            ActiveAlerts = (await alertsTask).Select(a => new AlertDto { Name = a.Name, Severity = a.Severity, Summary = a.Summary, Description = a.Description, State = a.State, ActiveSince = a.ActiveSince, Instance = a.Labels.GetValueOrDefault("instance") }).ToList(),
            GeneratedAtUtc = DateTime.UtcNow,
        };
    }

    private async Task<ServerStatusDto> ServerAsync(string url, MonitoredServerOptions server, CancellationToken ct)
    {
        var i = Escape(server.Instance);
        Task<double?> Q(string query) => prometheus.QueryScalarAsync(url, query, ct);
        var up = Q($"up{{instance=\"{i}\"}}"); var fresh = Q($"time() - timestamp(up{{instance=\"{i}\"}})");
        var cores = Q($"count(node_cpu_seconds_total{{instance=\"{i}\",mode=\"idle\"}})");
        var cpu = Q($"100-(avg(rate(node_cpu_seconds_total{{instance=\"{i}\",mode=\"idle\"}}[2m]))*100)");
        var memory = Q($"100*(1-node_memory_MemAvailable_bytes{{instance=\"{i}\"}}/node_memory_MemTotal_bytes{{instance=\"{i}\"}})");
        var memoryTotal = Q($"node_memory_MemTotal_bytes{{instance=\"{i}\"}}/1048576");
        var swapTotal = Q($"node_memory_SwapTotal_bytes{{instance=\"{i}\"}}/1048576"); var swapFree = Q($"node_memory_SwapFree_bytes{{instance=\"{i}\"}}/1048576");
        var disk = Q($"100*(1-node_filesystem_avail_bytes{{instance=\"{i}\",mountpoint=\"/\",fstype!=\"tmpfs\"}}/node_filesystem_size_bytes{{instance=\"{i}\",mountpoint=\"/\",fstype!=\"tmpfs\"}})");
        var diskTotal = Q($"node_filesystem_size_bytes{{instance=\"{i}\",mountpoint=\"/\",fstype!=\"tmpfs\"}}/1073741824");
        var load = Q($"node_load1{{instance=\"{i}\"}}"); var uptime = Q($"time()-node_boot_time_seconds{{instance=\"{i}\"}}");
        var rx = Q($"rate(node_network_receive_bytes_total{{instance=\"{i}\",device=\"eth0\"}}[5m])*8/1000000"); var tx = Q($"rate(node_network_transmit_bytes_total{{instance=\"{i}\",device=\"eth0\"}}[5m])*8/1000000");
        var read = Q($"sum(rate(node_disk_read_bytes_total{{instance=\"{i}\"}}[5m]))/1048576"); var write = Q($"sum(rate(node_disk_written_bytes_total{{instance=\"{i}\"}}[5m]))/1048576");
        var services = prometheus.QueryVectorAsync(url, $"rn_service_active{{instance=\"{i}\"}}", ct);
        var containerCpu = prometheus.QueryVectorAsync(url, $"rn_container_cpu_percent{{instance=\"{i}\"}}", ct); var containerMemory = prometheus.QueryVectorAsync(url, $"rn_container_memory_bytes{{instance=\"{i}\"}}", ct);
        await Task.WhenAll(new Task[] { up, fresh, cores, cpu, memory, memoryTotal, swapTotal, swapFree, disk, diskTotal, load, uptime, rx, tx, read, write, services, containerCpu, containerMemory });
        var alive = (await up ?? 0) >= .5; var totalSwap = await swapTotal ?? 0; var serviceRows = await services;
        var memoryByContainer = (await containerMemory).Where(x => x.Labels.ContainsKey("name")).ToDictionary(x => x.Labels["name"], x => x.Value / 1048576d);
        return new ServerStatusDto
        {
            Name = server.Name, Hostname = server.Hostname, IsOnDemand = server.IsOnDemand, Reachable = alive, Error = alive ? null : server.IsOnDemand ? "Standby" : "No current Prometheus data.", ConfiguredServices = server.Services,
            AgentDataAgeSeconds = await fresh ?? 0, CpuCores = (int)(await cores ?? 0), CpuUsagePercent = await cpu ?? 0, MemoryUsedPercent = await memory ?? 0, MemoryTotalMb = await memoryTotal ?? 0,
            SwapTotalMb = totalSwap, SwapUsedPercent = totalSwap <= 0 ? 0 : 100 * (1 - (await swapFree ?? totalSwap) / totalSwap), DiskUsedPercent = await disk ?? 0, DiskTotalGb = await diskTotal ?? 0,
            LoadAverage1m = await load ?? 0, UptimeSeconds = (long)(await uptime ?? 0), NetworkRxMbps = await rx ?? 0, NetworkTxMbps = await tx ?? 0, DiskReadMbps = await read ?? 0, DiskWriteMbps = await write ?? 0,
            Services = serviceRows.Select(x => new MonitoredServiceDto { Name = x.Labels.GetValueOrDefault("name", "service"), Active = x.Value >= .5 }).ToList(),
            ContainerMetrics = (await containerCpu).Select(x => new ContainerMetricDto { Name = x.Labels.GetValueOrDefault("name", "container"), CpuPercent = x.Value, MemoryMb = memoryByContainer.GetValueOrDefault(x.Labels.GetValueOrDefault("name", "")) }).OrderByDescending(x => x.CpuPercent).ToList(),
        };
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
