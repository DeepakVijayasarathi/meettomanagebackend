using iucs.meettomanage.application.Dto.Settings;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>
    /// The marketing site's SEO titles/descriptions and analytics tag IDs (seo.*, analytics.*),
    /// edited from the marketing console. Marketing role only — deliberately not a
    /// HasPermission check, since Admin passes every one of those and these settings belong to
    /// the platform owner, not the academy. The site reads them anonymously via
    /// GET /api/settings/public.
    /// </summary>
    [ApiController]
    [Route("api/marketing/settings")]
    [Authorize(Roles = nameof(UserRole.Marketing))]
    public class MarketingSettingsController : ControllerBase
    {
        private readonly ISettingsService _settingsService;

        public MarketingSettingsController(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyList<SettingDto>>> Get(CancellationToken cancellationToken)
        {
            return Ok(await _settingsService.GetMarketingAsync(cancellationToken));
        }

        [HttpPut]
        public async Task<ActionResult<IReadOnlyList<SettingDto>>> Update(
            List<UpdateSettingRequest> updates,
            CancellationToken cancellationToken)
        {
            await _settingsService.UpsertMarketingAsync(updates, cancellationToken);
            return Ok(await _settingsService.GetMarketingAsync(cancellationToken));
        }
    }
}
