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
            if (updates.Any(u => u.Key.StartsWith("brand.", StringComparison.Ordinal)))
            {
                var current = (await _settingsService.GetAllAsync(cancellationToken)).ToDictionary(x => x.Key, x => x.Value);
                if (updates.Any(u => u.Key.StartsWith("brand.", StringComparison.Ordinal)
                    && (current.GetValueOrDefault(u.Key) ?? "") != (u.Value ?? "")))
                    await PlanFeatures.RequireAsync(HttpContext, EntitlementKeys.WhiteLabel, "Your own branding");
            }
            return Ok(await _settingsService.UpsertAsync(updates, cancellationToken));
        }
    }
}
