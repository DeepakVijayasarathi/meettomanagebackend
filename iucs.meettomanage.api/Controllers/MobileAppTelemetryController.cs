using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Communication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Controllers;

[ApiController, Route("api/app/installations"), Authorize]
public sealed class MobileAppTelemetryController(MeetToManageDbContext db) : ControllerBase
{
    private static readonly HashSet<string> Platforms = new(StringComparer.OrdinalIgnoreCase) { "android", "ios", "web" };
    private static readonly HashSet<string> ErrorCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "network", "timeout", "authentication", "configuration", "schedule", "classroom", "notification", "render", "unknown"
    };

    public sealed class HeartbeatRequest
    {
        [Required, StringLength(64, MinimumLength = 16)] public string InstallationId { get; set; } = null!;
        [Required, StringLength(16)] public string Platform { get; set; } = null!;
        [Required, StringLength(32)] public string AppVersion { get; set; } = null!;
        [StringLength(32)] public string? BuildNumber { get; set; }
        [StringLength(64)] public string? RuntimeVersion { get; set; }
        public bool NotificationsEnabled { get; set; }
        public bool BiometricLockEnabled { get; set; }
        public bool SuccessfulSync { get; set; }
        [StringLength(64)] public string? ErrorCategory { get; set; }
    }

    [HttpPost("heartbeat"), EnableRateLimiting("mobile-heartbeat")]
    public async Task<IActionResult> Heartbeat(HeartbeatRequest request, CancellationToken ct)
    {
        if (!AppAccessPolicy.IsParentApp(Request.Headers)) return BadRequest(new { detail = "This endpoint is only for the mobile family app." });
        if (!Platforms.Contains(request.Platform)) return BadRequest(new { detail = "Unsupported platform." });
        if (request.ErrorCategory is not null && !ErrorCategories.Contains(request.ErrorCategory))
            return BadRequest(new { detail = "Unsupported error category." });

        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "Unknown";
        var now = DateTime.UtcNow;
        var installation = await db.MobileAppInstallations.SingleOrDefaultAsync(x => x.InstallationId == request.InstallationId, ct);
        if (installation is null)
        {
            installation = new MobileAppInstallation { InstallationId = request.InstallationId, UserId = userId };
            db.MobileAppInstallations.Add(installation);
        }
        else if (installation.UserId != userId)
        {
            // A reinstall or changed account may reuse local storage only after an OS restore. Move ownership without
            // retaining a cross-family association.
            installation.UserId = userId;
            installation.LastErrorCategory = null;
            installation.HeartbeatCount = 0;
        }

        installation.Role = role;
        installation.Platform = request.Platform.ToLowerInvariant();
        installation.AppVersion = request.AppVersion.Trim();
        installation.BuildNumber = NullIfBlank(request.BuildNumber);
        installation.RuntimeVersion = NullIfBlank(request.RuntimeVersion);
        installation.NotificationsEnabled = request.NotificationsEnabled;
        installation.BiometricLockEnabled = request.BiometricLockEnabled;
        installation.LastSeenAtUtc = now;
        if (request.SuccessfulSync) installation.LastSuccessfulSyncAtUtc = now;
        installation.LastErrorCategory = request.ErrorCategory?.ToLowerInvariant();
        installation.HeartbeatCount++;
        // Opportunistic privacy retention: the indexed delete is cheap and means no raw installation record survives
        // more than 90 days without activity, even if a background scheduler is unavailable.
        await db.MobileAppInstallations.Where(x => x.LastSeenAtUtc < now.AddDays(-90)).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return Ok(new { installation.Id, installation.LastSeenAtUtc });
    }

    [HttpDelete("{installationId}")]
    public async Task<IActionResult> Unlink(string installationId, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var installation = await db.MobileAppInstallations.SingleOrDefaultAsync(x => x.InstallationId == installationId && x.UserId == userId, ct);
        if (installation is not null)
        {
            db.MobileAppInstallations.Remove(installation);
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
