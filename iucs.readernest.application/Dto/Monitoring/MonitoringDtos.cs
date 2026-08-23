namespace iucs.readernest.application.Dto.Monitoring
{
    /// <summary>One named process/container this server's agent was asked to watch.</summary>
    public class MonitoredServiceDto
    {
        public string Name { get; set; } = string.Empty;
        public bool Active { get; set; }
    }

    /// <summary>Live conference/participant counts, populated only for the Jitsi server.</summary>
    public class LiveCallSummaryDto
    {
        public int ActiveConferences { get; set; }
        public int TotalParticipants { get; set; }
    }

    /// <summary>
    /// One server's point-in-time health, as reported by its own rn-status agent. <see cref="Reachable"/>
    /// false means the agent couldn't be reached at all (server down, network issue, wrong token) —
    /// every other field is then meaningless/default and the UI should show it as unknown, not "0%".
    /// </summary>
    public class ServerStatusDto
    {
        public string Name { get; set; } = string.Empty;
        public string Hostname { get; set; } = string.Empty;
        public bool Reachable { get; set; }
        public string? Error { get; set; }
        public long UptimeSeconds { get; set; }
        public double LoadAverage1m { get; set; }
        public int CpuCores { get; set; }
        public double CpuUsagePercent { get; set; }
        public double MemoryUsedPercent { get; set; }
        public double MemoryTotalMb { get; set; }
        public double DiskUsedPercent { get; set; }
        public double DiskTotalGb { get; set; }
        public List<MonitoredServiceDto> Services { get; set; } = new();
        /// <summary>How long ago the agent itself last wrote its status file — a stale reading (agent stuck/cron dead) still reports <see cref="Reachable"/> true, so the UI needs this to flag it separately.</summary>
        public double AgentDataAgeSeconds { get; set; }
        public LiveCallSummaryDto? LiveCalls { get; set; }
    }

    /// <summary>Everything the Server Monitoring dashboard needs in one call.</summary>
    public class MonitoringSummaryDto
    {
        public List<ServerStatusDto> Servers { get; set; } = new();
        public bool ApiHealthy { get; set; }
        public bool DatabaseHealthy { get; set; }
        public double DatabaseLatencyMs { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }
}
