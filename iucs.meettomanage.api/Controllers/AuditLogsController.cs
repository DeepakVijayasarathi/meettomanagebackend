using System.Security.Claims;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Dto.Audit;
using iucs.meettomanage.application.Dto.Common;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>
    /// Read access to the audit trail. Admins and any user with Settings:View see the
    /// full platform-wide trail (matching the admin console's needs); everyone else — most
    /// notably Sub-Admins on their own Audit Log screen — is restricted to their own actions,
    /// since that screen is scoped to "actions you've taken", not a platform-wide log.
    /// </summary>
    [ApiController]
    [Route("api/audit-logs")]
    [Authorize]
    public class AuditLogsController : ControllerBase
    {
        private readonly IAuditLogService _auditLogService;

        public AuditLogsController(IAuditLogService auditLogService)
        {
            _auditLogService = auditLogService;
        }

        [HttpGet]
        public async Task<ActionResult<PagedResult<AuditLogDto>>> List(
            [FromQuery] string? entityName,
            [FromQuery] AuditAction? action,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            CancellationToken cancellationToken = default)
        {
            var hasPlatformView = User.IsInRole(nameof(UserRole.Admin))
                || User.HasClaim(JwtTokenService.PermissionClaimType, $"{PermissionModule.Settings}:{PermissionAction.View}");

            Guid? restrictToActorId = null;
            if (!hasPlatformView)
            {
                var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!Guid.TryParse(userIdClaim, out var userId))
                {
                    return Forbid();
                }

                restrictToActorId = userId;
            }

            return Ok(await _auditLogService.ListAsync(entityName, action, page, pageSize, restrictToActorId, cancellationToken));
        }
    }
}
