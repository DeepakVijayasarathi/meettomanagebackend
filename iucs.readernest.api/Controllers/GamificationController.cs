using System.Security.Claims;
using iucs.readernest.application.Dto.Sessions;
using iucs.readernest.application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.readernest.api.Controllers
{
    /// <summary>
    /// Persistent gamification: stars/badges/milestones earned in live classes.
    /// A session participant can post their own star awards from the classroom; a
    /// Badge/session-less award requires Teacher/Admin. The leaderboard is readable
    /// by every portal (names only, no PII).
    /// </summary>
    [ApiController]
    [Route("api/gamification")]
    [Authorize]
    public class GamificationController : ControllerBase
    {
        private readonly IGamificationService _gamificationService;

        public GamificationController(IGamificationService gamificationService)
        {
            _gamificationService = gamificationService;
        }

        [HttpPost("awards")]
        public async Task<ActionResult<IReadOnlyList<AwardDto>>> Grant(
            GrantAwardRequest request,
            CancellationToken cancellationToken)
        {
            var callerUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return Ok(await _gamificationService.GrantAsync(callerUserId, request, cancellationToken));
        }

        [HttpGet("leaderboard")]
        public async Task<ActionResult<IReadOnlyList<LeaderboardEntryDto>>> Leaderboard(
            [FromQuery] Guid? sessionId,
            [FromQuery] int top = 10,
            CancellationToken cancellationToken = default)
        {
            return Ok(await _gamificationService.GetLeaderboardAsync(sessionId, top, cancellationToken));
        }
    }
}
