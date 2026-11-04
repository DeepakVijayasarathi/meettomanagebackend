using System.Security.Claims;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Learning;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>
    /// Homework and tests. Teachers manage work for their own batches; Admin and Sub Admins with
    /// Course &amp; Batch access manage every batch; parents see and hand in work for their children.
    /// </summary>
    [ApiController]
    [Route("api/assignments")]
    public class AssignmentsController : ControllerBase
    {
        private const string StaffRoles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)},{nameof(UserRole.Teacher)}";
        private const long MaxUploadBytes = AssignmentService.MaxFileBytes + 64 * 1024;

        private readonly IAssignmentService _assignments;
        private readonly IFileStorage _storage;

        public AssignmentsController(IAssignmentService assignments, IFileStorage storage)
        {
            _assignments = assignments;
            _storage = storage;
        }

        // ------------------------------------------------------------ teachers & staff

        [HttpGet]
        [Authorize(Roles = StaffRoles)]
        public async Task<ActionResult<IReadOnlyList<AssignmentDto>>> List([FromQuery] Guid? batchId, CancellationToken cancellationToken) =>
            Ok(await _assignments.ListAsync(Actor(), batchId, cancellationToken));

        /// <summary>Active batches the caller can set work for: their own as a teacher, all of them as staff.</summary>
        [HttpGet("batches")]
        [Authorize(Roles = StaffRoles)]
        public async Task<ActionResult<IReadOnlyList<AssignableBatchDto>>> Batches(CancellationToken cancellationToken) =>
            Ok(await _assignments.ListBatchesAsync(Actor(), cancellationToken));

        [HttpGet("{id:guid}")]
        [Authorize(Roles = StaffRoles)]
        public async Task<ActionResult<AssignmentDetailDto>> Get(Guid id, CancellationToken cancellationToken) =>
            Ok(await _assignments.GetAsync(Actor(), id, cancellationToken));

        [HttpPost]
        [Authorize(Roles = StaffRoles)]
        [RequestSizeLimit(MaxUploadBytes)]
        public async Task<ActionResult<AssignmentDetailDto>> Create([FromForm] SaveAssignmentRequest request, IFormFile? file, CancellationToken cancellationToken)
        {
            await using var upload = file?.OpenReadStream();
            var created = await _assignments.CreateAsync(Actor(), request, Upload(file, upload), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }

        [HttpPut("{id:guid}")]
        [Authorize(Roles = StaffRoles)]
        [RequestSizeLimit(MaxUploadBytes)]
        public async Task<ActionResult<AssignmentDetailDto>> Update(Guid id, [FromForm] SaveAssignmentRequest request, IFormFile? file, CancellationToken cancellationToken)
        {
            await using var upload = file?.OpenReadStream();
            return Ok(await _assignments.UpdateAsync(Actor(), id, request, Upload(file, upload), cancellationToken));
        }

        [HttpDelete("{id:guid}")]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        {
            await _assignments.DeleteAsync(Actor(), id, cancellationToken);
            return NoContent();
        }

        [HttpPost("submissions/{submissionId:guid}/grade")]
        [Authorize(Roles = StaffRoles)]
        public async Task<ActionResult<AssignmentSubmissionDto>> Grade(Guid submissionId, GradeSubmissionRequest request, CancellationToken cancellationToken) =>
            Ok(await _assignments.GradeAsync(Actor(), submissionId, request, cancellationToken));

        /// <summary>Makes a test's marks visible to families and notifies them.</summary>
        [HttpPost("{id:guid}/release-results")]
        [Authorize(Roles = StaffRoles)]
        public async Task<ActionResult<AssignmentDetailDto>> ReleaseResults(Guid id, CancellationToken cancellationToken) =>
            Ok(await _assignments.ReleaseResultsAsync(Actor(), id, cancellationToken));

        [HttpGet("{id:guid}/attachment")]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> Attachment(Guid id, CancellationToken cancellationToken) =>
            await SendAsync(await _assignments.GetAttachmentForStaffAsync(Actor(), id, cancellationToken), cancellationToken);

        [HttpGet("submissions/{submissionId:guid}/file")]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> SubmissionFile(Guid submissionId, CancellationToken cancellationToken) =>
            await SendAsync(await _assignments.GetSubmissionFileForStaffAsync(Actor(), submissionId, cancellationToken), cancellationToken);

        // ------------------------------------------------------------ families

        [HttpGet("mine")]
        [Authorize(Roles = nameof(UserRole.Parent))]
        public async Task<ActionResult<IReadOnlyList<FamilyAssignmentDto>>> Mine([FromQuery] Guid? childId, CancellationToken cancellationToken) =>
            Ok(await _assignments.ListForParentAsync(UserId(), childId, cancellationToken));

        [HttpPost("mine/{submissionId:guid}/submit")]
        [Authorize(Roles = nameof(UserRole.Parent))]
        [RequestSizeLimit(MaxUploadBytes)]
        public async Task<ActionResult<FamilyAssignmentDto>> Submit(Guid submissionId, [FromForm] SubmitAssignmentRequest request, IFormFile? file, CancellationToken cancellationToken)
        {
            await using var upload = file?.OpenReadStream();
            return Ok(await _assignments.SubmitAsync(UserId(), submissionId, request, Upload(file, upload), cancellationToken));
        }

        [HttpGet("mine/{submissionId:guid}/attachment")]
        [Authorize(Roles = nameof(UserRole.Parent))]
        public async Task<IActionResult> MyAttachment(Guid submissionId, CancellationToken cancellationToken) =>
            await SendAsync(await _assignments.GetAttachmentForParentAsync(UserId(), submissionId, cancellationToken), cancellationToken);

        [HttpGet("mine/{submissionId:guid}/file")]
        [Authorize(Roles = nameof(UserRole.Parent))]
        public async Task<IActionResult> MySubmissionFile(Guid submissionId, CancellationToken cancellationToken) =>
            await SendAsync(await _assignments.GetSubmissionFileForParentAsync(UserId(), submissionId, cancellationToken), cancellationToken);

        // ------------------------------------------------------------ helpers

        private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        /// <summary>Admin, or a Sub Admin granted Course &amp; Batch access, works across every batch; anyone else as a teacher.</summary>
        private AssignmentActor Actor() => new(UserId(),
            User.IsInRole(nameof(UserRole.Admin))
            || User.HasClaim(JwtTokenService.PermissionClaimType, $"{PermissionModule.CourseBatchManagement}:{PermissionAction.Edit}"));

        private static UploadedFile? Upload(IFormFile? file, Stream? stream) =>
            file is null || stream is null ? null : new UploadedFile(stream, file.FileName, file.Length);

        private async Task<IActionResult> SendAsync(AttachmentDownload download, CancellationToken cancellationToken)
        {
            var stream = await _storage.OpenReadAsync(download.Path, cancellationToken);
            if (stream is null) return NotFound(new ProblemDetails { Status = 404, Title = "Not Found", Detail = "The stored file is missing." });
            if (!new FileExtensionContentTypeProvider().TryGetContentType(download.FileName, out var contentType))
                contentType = "application/octet-stream";
            return File(stream, contentType, download.FileName);
        }
    }
}
