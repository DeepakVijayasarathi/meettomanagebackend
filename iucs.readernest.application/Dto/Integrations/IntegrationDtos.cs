using System.ComponentModel.DataAnnotations;
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

    /// <remarks>
    /// Lengths mirror the Integration entity's columns. Without them an over-long field
    /// passes model validation and only fails at SaveChanges, where a varchar overflow
    /// surfaces as an unhandled DbUpdateException (a 500) instead of a 400.
    /// </remarks>
    public class SaveIntegrationRequest
    {
        [Required]
        [MaxLength(64)]
        public string Key { get; set; } = null!;

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = null!;

        [Required]
        public IntegrationCategory Category { get; set; }

        [MaxLength(500)]
        public string? Description { get; set; }

        public bool IsEnabled { get; set; }

        public Dictionary<string, string?> Config { get; set; } = [];
    }
}
