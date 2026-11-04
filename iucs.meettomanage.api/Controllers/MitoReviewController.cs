using System.Security.Claims;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers;

/// <summary>
/// Staff side of the Mito companion: usage dashboard and review of answers families flagged. Teachers see only
/// students in their own batches; Admin and Sub Admins with Reports access see the whole academy.
/// </summary>
[ApiController]
[Route("api/mito-review")]
[Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)},{nameof(UserRole.Teacher)}")]
public class MitoReviewController(IMitoUsageService usage, ISettingsService settings) : ControllerBase
{
    [HttpGet("insights")]
    public async Task<ActionResult<MitoInsightsDto>> Insights([FromQuery] int days = 30, CancellationToken cancellationToken = default)
    {
        var limit = (await settings.GetAllAsync(cancellationToken)).FirstOrDefault(x => x.Key == "app.mito.dailyLimit")?.Value;
        return Ok(await usage.GetInsightsAsync(days, MitoController.DailyLimit(limit), TeacherScope(), cancellationToken));
    }

    [HttpGet("flags")]
    public async Task<ActionResult<IReadOnlyList<MitoFlagDto>>> Flags([FromQuery] bool open = true, CancellationToken cancellationToken = default) =>
        Ok(await usage.ListFlagsAsync(open, TeacherScope(), cancellationToken));

    [HttpPost("flags/{id:guid}/resolve")]
    public async Task<ActionResult<MitoFlagDto>> Resolve(Guid id, ResolveMitoFlagRequest request, CancellationToken cancellationToken) =>
        Ok(await usage.ResolveFlagAsync(id, UserId(), TeacherScope(), request.Resolution, cancellationToken));

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Null (whole academy) for Admin or a Sub Admin with Reports access; otherwise limited to the teacher's own students.</summary>
    private Guid? TeacherScope() =>
        User.IsInRole(nameof(UserRole.Admin))
        || User.HasClaim(JwtTokenService.PermissionClaimType, $"{PermissionModule.ReportsAnalytics}:{PermissionAction.View}")
            ? null
            : UserId();
}
