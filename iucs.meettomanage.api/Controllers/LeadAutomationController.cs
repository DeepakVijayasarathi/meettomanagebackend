using iucs.meettomanage.api.Marketing;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace iucs.meettomanage.api.Controllers;

/// <summary>The marketing console's Lead automation page: funnel, what ran, and the on/off switches.</summary>
[ApiController, Route("api/marketing/lead-automation"), Authorize(Roles = nameof(UserRole.Marketing))]
public sealed class LeadAutomationController(LeadAutomationService automation) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<LeadAutomationOverview>> Overview([FromQuery] int days = 90, CancellationToken ct = default) =>
        Ok(await automation.OverviewAsync(days, ct));

    [HttpPut("settings")]
    public async Task<ActionResult<Dictionary<string, string>>> SaveSettings(Dictionary<string, string> values, CancellationToken ct)
    {
        await automation.SaveSettingsAsync(values, ct);
        return Ok(await automation.SettingsAsync(ct));
    }

    /// <summary>Runs the scheduled automations now instead of waiting up to ten minutes.</summary>
    [HttpPost("run"), EnableRateLimiting("login")]
    public async Task<IActionResult> RunNow(CancellationToken ct)
    {
        await automation.RunAsync(DateTime.UtcNow, ct);
        return NoContent();
    }
}
