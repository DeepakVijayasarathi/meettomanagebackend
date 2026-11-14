using iucs.meettomanage.api.Auth;
using iucs.meettomanage.api.Saas;
using iucs.meettomanage.application.Dto.Settings;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    [ApiController]
    [Route("api/settings")]
    public class SettingsController : ControllerBase
    {
        private readonly ISettingsService _settingsService;

        public SettingsController(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        /// <summary>Branding and other public settings; anonymous so the login screen can brand itself.</summary>
        [HttpGet("public")]
        [AllowAnonymous]
        public async Task<ActionResult<IReadOnlyList<SettingDto>>> GetPublic(CancellationToken cancellationToken)
        {
            return Ok(await _settingsService.GetPublicAsync(cancellationToken));
        }

        [HttpGet]
        [HasPermission(PermissionModule.Settings, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<SettingDto>>> GetAll(CancellationToken cancellationToken)
        {
            return Ok(await _settingsService.GetAllAsync(cancellationToken));
        }

        /// <summary>Bulk upsert; returns the full settings list after saving.</summary>
        [HttpPut]
        [HasPermission(PermissionModule.Settings, PermissionAction.Edit)]
        public async Task<ActionResult<IReadOnlyList<SettingDto>>> Update(
            List<UpdateSettingRequest> updates,
            CancellationToken cancellationToken)
        {
            // Changing your own branding (brand.*) is a paid feature; re-saving the page with branding unchanged is not.
            // The Settings page sends every key, filling never-stored ones with the platform default, so a missing
            // stored value counts as that default (otherwise a Free academy could not save any setting at all).
            if (updates.Any(u => u.Key.StartsWith("brand.", StringComparison.Ordinal)))
            {
                var current = (await _settingsService.GetAllAsync(cancellationToken)).ToDictionary(x => x.Key, x => x.Value);
                if (updates.Any(u => u.Key.StartsWith("brand.", StringComparison.Ordinal) && BrandChanged(current, u.Key, u.Value)))
                    await PlanFeatures.RequireAsync(HttpContext, EntitlementKeys.WhiteLabel, "your own branding");
            }
            return Ok(await _settingsService.UpsertAsync(updates, cancellationToken));
        }

        /// <summary>Platform defaults for the brand.* keys (match DatabaseInitializer and the Settings page fallbacks).</summary>
        private static readonly IReadOnlyDictionary<string, string> BrandDefaults = new Dictionary<string, string>
        {
            ["brand.name"] = "Meet to Manage",
            ["brand.logoUrl"] = "",
            ["brand.darkLogoUrl"] = "",
            ["brand.faviconUrl"] = "",
            ["brand.primaryColor"] = "#1E3A5F",
            ["brand.accentColor"] = "#E63329",
            ["brand.fontFamily"] = "DM Sans",
            ["brand.headingFont"] = "Plus Jakarta Sans",
        };

        public static bool BrandChanged(IReadOnlyDictionary<string, string?> current, string key, string? value)
        {
            // Only a key that was never stored falls back to the default; a stored empty value stays empty.
            var stored = current.TryGetValue(key, out var v) ? v ?? "" : BrandDefaults.GetValueOrDefault(key, "");
            return !string.Equals(stored.Trim(), (value ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
