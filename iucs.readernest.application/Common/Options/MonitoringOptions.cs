namespace iucs.readernest.application.Common.Options
{
    /// <summary>
    /// Binds the "Monitoring" config section. The dashboard reads everything from the
    /// Prometheus instance already running on the app server (see docs/PROVISIONING.md's
    /// monitoring section) — node-exporter for OS metrics on every server, plus a small
    /// textfile-collector cron script per server that publishes service up/down and (on the
    /// Jitsi box) live-conference counts as plain Prometheus metrics. No credentials belong
    /// here: Prometheus itself has no auth in front of it today (internal network only).
    /// </summary>
    public class MonitoringOptions
    {
        public const string SectionName = "Monitoring";

        /// <summary>Base URL of the Prometheus HTTP API, e.g. "http://prometheus:9090" (Docker network name) or "http://204.168.140.222:9090".</summary>
        public string PrometheusBaseUrl { get; set; } = string.Empty;

        public List<MonitoredServerOptions> Servers { get; set; } = new();
    }

    public class MonitoredServerOptions
    {
        /// <summary>Display name shown on the dashboard, e.g. "Jitsi / Video".</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Display hostname, e.g. "thereadernest.co.in".</summary>
        public string Hostname { get; set; } = string.Empty;

        /// <summary>The Prometheus "instance" label this server's node-exporter (and textfile facts) are scraped under.</summary>
        public string Instance { get; set; } = string.Empty;

        /// <summary>rn_service_active{name=...} values published by this server's textfile-collector script.</summary>
        public List<string> Services { get; set; } = new();

        /// <summary>True only for the Jitsi box — queries rn_jitsi_conferences/rn_jitsi_participants for this instance.</summary>
        public bool TracksLiveCalls { get; set; }
    }
}
