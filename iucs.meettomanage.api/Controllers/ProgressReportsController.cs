using System.Security.Claims;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Dto.Communication;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>
    /// Monthly per-child progress reports: staff write the content, review it, and send
    /// it to the parent by email. A month's drafts are seeded automatically on the 1st.
    /// </summary>
    [ApiController]
    [Route("api/progress-reports")]
    public class ProgressReportsController : ControllerBase
    {
        private readonly IProgressReportService _progressReportService;

        public ProgressReportsController(IProgressReportService progressReportService)
        {
            _progressReportService = progressReportService;
        }

        /// <summary>Visibility rule: admin (or granted sub-admin) sees every child's reports.</summary>
        [HttpGet]
        [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)}")]
        [HasPermission(PermissionModule.Communication, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<ProgressReportDto>>> List(
            [FromQuery] int? year,
            [FromQuery] int? month,
            [FromQuery] Guid? childId,
            CancellationToken cancellationToken)
        {
            return Ok(await _progressReportService.ListAsync(year, month, childId, cancellationToken));
        }

        /// <summary>Visibility rule: a parent sees only their own children's sent reports.</summary>
        [HttpGet("mine")]
        [Authorize(Roles = nameof(UserRole.Parent))]
        public async Task<ActionResult<IReadOnlyList<ProgressReportDto>>> Mine(CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return Ok(await _progressReportService.ListForParentUserAsync(userId, cancellationToken));
        }

        [HttpPut("{id:guid}")]
        [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)}")]
        [HasPermission(PermissionModule.Communication, PermissionAction.Edit)]
        public async Task<ActionResult<ProgressReportDto>> SaveContent(
            Guid id,
            SaveProgressReportContentRequest request,
            CancellationToken cancellationToken)
        {
            return Ok(await _progressReportService.SaveContentAsync(id, request, cancellationToken));
        }

        /// <summary>Emails the report to the parent and locks it against further edits.</summary>
        [HttpPost("{id:guid}/send")]
        [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)}")]
        [HasPermission(PermissionModule.Communication, PermissionAction.Create)]
        public async Task<ActionResult<ProgressReportDto>> Send(Guid id, CancellationToken cancellationToken)
        {
            return Ok(await _progressReportService.SendAsync(id, cancellationToken));
        }
    }
}
