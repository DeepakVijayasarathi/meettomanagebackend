using System.Text.Json;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Common.Options;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace iucs.meettomanage.api.Saas;

public sealed record PlatformMonitoringSettings(string PrometheusBaseUrl, int RefreshSeconds, List<PlatformMonitoredServer> Servers);
public sealed record PlatformMonitoredServer(Guid Id, string Name, string Hostname, string Instance, List<string> Services,
    bool TracksLiveCalls, bool IsOnDemand, bool MaintenanceMode, string SshHost, int SshPort, string SshUsername,
    bool HasSshPassword, string? SshPassword, int CpuWarningPercent = 80, int MemoryWarningPercent = 85, int DiskWarningPercent = 85);

/// <summary>Master-database inventory. SSH secrets are AES-GCM encrypted and never returned by GET.</summary>
public sealed class PlatformMonitoringSettingsService(IDbContextFactory<MasterDbContext> factory, IPinVault vault, IOptions<MonitoringOptions> fallback)
{
    private const string Root = "monitoring.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PlatformMonitoringSettings> GetSafeAsync(CancellationToken ct = default)
    {
        var resolved = await GetResolvedAsync(ct);
        return resolved with { Servers = resolved.Servers.Select(x => x with { SshPassword = null }).ToList() };
    }

    public async Task<PlatformMonitoringSettings> GetResolvedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.PlatformSettings.AsNoTracking().Where(x => x.Key.StartsWith(Root)).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        var serverRows = rows.Where(x => x.Key.StartsWith(Root + "server.") && !x.Key.EndsWith(".secret", StringComparison.Ordinal)).ToList();
        if (serverRows.Count == 0)
        {
            return new(fallback.Value.PrometheusBaseUrl, 30, fallback.Value.Servers.Select(s => new PlatformMonitoredServer(Guid.NewGuid(), s.Name, s.Hostname, s.Instance,
                s.Services, s.TracksLiveCalls, s.IsOnDemand, false, s.SshHost, s.SshPort, s.SshUsername, !string.IsNullOrWhiteSpace(s.SshPassword), s.SshPassword)).ToList());
        }
        var servers = new List<PlatformMonitoredServer>();
        foreach (var row in serverRows)
        {
            try
            {
                var server = JsonSerializer.Deserialize<PlatformMonitoredServer>(row.Value ?? "", Json);
                if (server is null) continue;
                var secret = rows.GetValueOrDefault($"{Root}server.{server.Id:N}.secret");
                var password = string.IsNullOrWhiteSpace(secret) ? null : vault.TryReveal(secret);
                servers.Add(server with { HasSshPassword = password is not null, SshPassword = password });
            }
            catch (JsonException) { }
        }
        var refresh = int.TryParse(rows.GetValueOrDefault(Root + "refreshSeconds"), out var seconds) ? Math.Clamp(seconds, 15, 300) : 30;
        return new(rows.GetValueOrDefault(Root + "prometheusBaseUrl") ?? fallback.Value.PrometheusBaseUrl, refresh, servers);
    }

    public async Task<string?> SaveAsync(PlatformMonitoringSettings input, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(input.PrometheusBaseUrl, UriKind.Absolute, out var prometheus) || prometheus.Scheme is not ("http" or "https")) return "Enter a valid Prometheus HTTP or HTTPS URL.";
        if (input.Servers.Count > 50) return "A maximum of 50 servers can be monitored.";
        if (input.Servers.Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.Servers.Count) return "Server names must be unique.";
        foreach (var s in input.Servers)
        {
            if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Instance)) return "Every server needs a name and Prometheus instance label.";
            if (s.SshPort is < 1 or > 65535) return $"{s.Name} has an invalid SSH port.";
            if (s.CpuWarningPercent is < 1 or > 100 || s.MemoryWarningPercent is < 1 or > 100 || s.DiskWarningPercent is < 1 or > 100) return $"{s.Name} thresholds must be between 1 and 100.";
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.PlatformSettings.Where(x => x.Key.StartsWith(Root)).ToDictionaryAsync(x => x.Key, ct);
        var keep = new HashSet<string> { Root + "prometheusBaseUrl", Root + "refreshSeconds" };
        Upsert(Root + "prometheusBaseUrl", input.PrometheusBaseUrl.Trim()); Upsert(Root + "refreshSeconds", Math.Clamp(input.RefreshSeconds, 15, 300).ToString());
        foreach (var raw in input.Servers)
        {
            var id = raw.Id == Guid.Empty ? Guid.NewGuid() : raw.Id;
            var server = raw with { Id = id, Name = raw.Name.Trim(), Hostname = raw.Hostname.Trim(), Instance = raw.Instance.Trim(), SshHost = raw.SshHost.Trim(), SshUsername = raw.SshUsername.Trim(), SshPassword = null };
            var key = $"{Root}server.{id:N}"; var secretKey = key + ".secret"; keep.Add(key); keep.Add(secretKey);
            var serialized = JsonSerializer.Serialize(server, Json);
            if (serialized.Length > 2000) return $"{server.Name} has too much configuration data.";
            Upsert(key, serialized);
            if (!string.IsNullOrWhiteSpace(raw.SshPassword)) Upsert(secretKey, vault.Protect(raw.SshPassword));
            else if (!existing.ContainsKey(secretKey) && raw.HasSshPassword)
            {
                var inherited = fallback.Value.Servers.FirstOrDefault(x => x.Name.Equals(raw.Name, StringComparison.OrdinalIgnoreCase))?.SshPassword;
                if (!string.IsNullOrWhiteSpace(inherited)) Upsert(secretKey, vault.Protect(inherited));
            }
            else if (existing.ContainsKey(secretKey)) keep.Add(secretKey);
        }
        foreach (var stale in existing.Where(x => x.Key.StartsWith(Root + "server.") && !keep.Contains(x.Key)).Select(x => x.Value)) db.PlatformSettings.Remove(stale);
        await db.SaveChangesAsync(ct); return null;

        void Upsert(string key, string? value)
        {
            keep.Add(key);
            if (!existing.TryGetValue(key, out var row)) { row = new PlatformSetting { Key = key }; db.PlatformSettings.Add(row); existing[key] = row; }
            row.Value = value; row.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    public async Task<(bool Ok, string Message, long LatencyMs)> TestSshAsync(Guid id, CancellationToken ct = default)
    {
        var server = (await GetResolvedAsync(ct)).Servers.FirstOrDefault(x => x.Id == id);
        if (server is null) return (false, "Server not found.", 0);
        if (string.IsNullOrWhiteSpace(server.SshHost) || string.IsNullOrWhiteSpace(server.SshUsername) || string.IsNullOrWhiteSpace(server.SshPassword)) return (false, "Save the SSH host, username and password first.", 0);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = new SshClient(server.SshHost, server.SshPort, server.SshUsername, server.SshPassword) { ConnectionInfo = { Timeout = TimeSpan.FromSeconds(8) } };
            await Task.Run(client.Connect, ct); using var command = client.CreateCommand("printf monitoring-ok"); var result = command.Execute(); client.Disconnect();
            timer.Stop(); return (result == "monitoring-ok", result == "monitoring-ok" ? "SSH connection successful." : "SSH connected but the health command failed.", timer.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { timer.Stop(); return (false, "SSH connection failed: " + ex.Message, timer.ElapsedMilliseconds); }
    }

    public static List<MonitoredServerOptions> ToOptions(PlatformMonitoringSettings settings) => settings.Servers.Select(x => new MonitoredServerOptions
    { Name = x.Name, Hostname = x.Hostname, Instance = x.Instance, Services = x.Services, TracksLiveCalls = x.TracksLiveCalls, IsOnDemand = x.IsOnDemand, SshHost = x.SshHost, SshPort = x.SshPort, SshUsername = x.SshUsername, SshPassword = x.SshPassword ?? "" }).ToList();
}
