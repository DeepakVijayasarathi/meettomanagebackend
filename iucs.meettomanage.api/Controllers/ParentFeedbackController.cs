using System.Security.Claims;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Dto.Admission;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>Parents rate the demo class / finished course; Admin and Admission read the results.</summary>
    [ApiController]
    [Route("api/parent-feedback")]
    public class ParentFeedbackController : ControllerBase
    {
        private readonly IParentFeedbackService _feedback;

        public ParentFeedbackController(IParentFeedbackService feedback)
        {
            _feedback = feedback;
        }

        /// <summary>What the signed-in parent should be asked about right now (empty = nothing).</summary>
        [HttpGet("pending")]
        [Authorize(Roles = nameof(UserRole.Parent))]
        public async Task<ActionResult<IReadOnlyList<PendingParentFeedbackDto>>> Pending(CancellationToken cancellationToken)
        {
            return Ok(await _feedback.GetPendingAsync(UserId(), cancellationToken));
        }

        [HttpPost]
        [Authorize(Roles = nameof(UserRole.Parent))]
        public async Task<IActionResult> Submit(SubmitParentFeedbackRequest request, CancellationToken cancellationToken)
        {
            await _feedback.SubmitAsync(UserId(), request, cancellationToken);
            return NoContent();
        }

        [HttpGet]
        [HasPermission(PermissionModule.Admission, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<ParentFeedbackDto>>> List(
            [FromQuery] ParentFeedbackKind? kind, CancellationToken cancellationToken)
        {
            return Ok(await _feedback.ListAsync(kind, cancellationToken));
        }

        private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }
}
