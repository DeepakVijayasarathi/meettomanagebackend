using System.ComponentModel.DataAnnotations;
using iucs.readernest.domain.Enums;

namespace iucs.readernest.application.Dto.Settings
{
    public class SettingDto
    {
        public SettingCategory Category { get; set; }

        public string Key { get; set; } = null!;

        public string? Value { get; set; }

        public bool IsPublic { get; set; }
    }

    /// <summary>Single key update; unknown keys are created under the given category.</summary>
    /// <remarks>
    /// Lengths mirror the AppSetting entity's columns. SettingsService re-checks both (it is
    /// also reachable outside MVC model binding), but having them here fails the request at
    /// the boundary with a per-field 400 instead of a single service-level message.
    /// </remarks>
    public class UpdateSettingRequest
    {
        [Required]
        [MaxLength(100)]
        public string Key { get; set; } = null!;

        [MaxLength(2000)]
        public string? Value { get; set; }

        public SettingCategory Category { get; set; } = SettingCategory.General;

        public bool IsPublic { get; set; }
    }
}
