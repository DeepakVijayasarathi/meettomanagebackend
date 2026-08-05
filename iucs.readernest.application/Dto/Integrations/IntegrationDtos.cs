using iucs.readernest.domain.Enums;

namespace iucs.readernest.application.Dto.Integrations
{
    public class IntegrationDto
    {
        public Guid Id { get; set; }

        public string Key { get; set; } = null!;

        public string Name { get; set; } = null!;

        public IntegrationCategory Category { get; set; }

        public string? Description { get; set; }

        public bool IsEnabled { get; set; }

        /// <summary>Provider-specific fields, e.g. apiKey/apiSecret/webhookUrl.</summary>
        public Dictionary<string, string?> Config { get; set; } = [];

        public bool IsSystem { get; set; }
    }

    public class SaveIntegrationRequest
    {
        public string Key { get; set; } = null!;

        public string Name { get; set; } = null!;

        public IntegrationCategory Category { get; set; }

        public string? Description { get; set; }

        public bool IsEnabled { get; set; }

        public Dictionary<string, string?> Config { get; set; } = [];
    }

    /// <summary>
    /// Non-secret slice of the "jitsi" Integration's config exposed to whoever is
    /// about to join a live class (any authenticated role), so the client knows
    /// which domain to embed and whether to auto-start recording on host join.
    /// </summary>
    public class JitsiSettingsDto
    {
        public string Domain { get; set; } = null!;

        public bool AutoRecordEnabled { get; set; }
    }
}
