using System.Security.Claims;
using System.Text;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Dto.Enrollment;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    [ApiController]
    [Route("api/enrollment-forms")]
    public class EnrollmentFormsController : ControllerBase
    {
        private readonly IEnrollmentService _enrollmentService;

        public EnrollmentFormsController(IEnrollmentService enrollmentService)
        {
            _enrollmentService = enrollmentService;
        }

        /// <summary>Mandatory first-login enrollment form submission by the parent.</summary>
        [HttpPost]
        [Authorize(Roles = nameof(UserRole.Parent))]
        public async Task<ActionResult<EnrollmentFormDto>> Submit(
            SubmitEnrollmentFormRequest request,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return Ok(await _enrollmentService.SubmitAsync(userId, request, cancellationToken));
        }

        /// <summary>The parent's own submissions and their review state.</summary>
        [HttpGet("mine")]
        [Authorize(Roles = nameof(UserRole.Parent))]
        public async Task<ActionResult<IReadOnlyList<EnrollmentFormDto>>> Mine(CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return Ok(await _enrollmentService.ListForParentUserAsync(userId, cancellationToken));
        }

        // Gated on Admission, not UserManagement: enrollment submissions carry raw
        // family/child JSON, and UserManagement:View is also granted to Coordinator for
        // an unrelated reason (browsing users for scheduling) — that shared claim would
        // otherwise let Coordinator read/download every family's enrollment data too.
        [HttpGet]
        [HasPermission(PermissionModule.Admission, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<EnrollmentFormDto>>> List(
            [FromQuery] EnrollmentFormStatus? status,
            CancellationToken cancellationToken)
        {
            return Ok(await _enrollmentService.ListAsync(status, cancellationToken));
        }

        /// <summary>Approval creates the Child record and unlocks the parent dashboard.</summary>
        [HttpPost("{id:guid}/review")]
        [HasPermission(PermissionModule.Admission, PermissionAction.Approve)]
        public async Task<ActionResult<EnrollmentFormDto>> Review(
            Guid id,
            ReviewEnrollmentFormRequest request,
            CancellationToken cancellationToken)
        {
            return Ok(await _enrollmentService.ReviewAsync(id, request, cancellationToken));
        }

        /// <summary>Admin edits the submitted answers before approval.</summary>
        [HttpPut("{id:guid}")]
        [HasPermission(PermissionModule.Admission, PermissionAction.Edit)]
        public async Task<ActionResult<EnrollmentFormDto>> Update(
            Guid id,
            SubmitEnrollmentFormRequest request,
            CancellationToken cancellationToken)
        {
            return Ok(await _enrollmentService.UpdateFormDataAsync(id, request, cancellationToken));
        }

        /// <summary>Admin download of the submitted form as a JSON document.</summary>
        [HttpGet("{id:guid}/download")]
        [HasPermission(PermissionModule.Admission, PermissionAction.View)]
        public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
        {
            var form = await _enrollmentService.GetAsync(id, cancellationToken);
            return File(Encoding.UTF8.GetBytes(form.FormDataJson), "application/json", $"enrollment-{form.Id}.json");
        }
    }
}
