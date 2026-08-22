using iucs.readernest.api.Auth;
using iucs.readernest.application.Dto.Courses;
using iucs.readernest.application.Services;
using iucs.readernest.domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace iucs.readernest.api.Controllers
{
    /// <summary>
    /// Business departments (Phonics, Maths, and any admin-added ones) — previously a fixed
    /// 2-value enum, now a plain admin-managed list so a new subject area (Hindi, Abacus,
    /// Spoken English, ...) doesn't need a code change + redeploy to add. Each department
    /// still needs its own payment gateway account wired up separately under Payment Gateway
    /// Mapping before real invoices can route through it.
    /// </summary>
    [ApiController]
    [Route("api/departments")]
    public class DepartmentsController : ControllerBase
    {
        private readonly IDepartmentService _departmentService;

        public DepartmentsController(IDepartmentService departmentService)
        {
            _departmentService = departmentService;
        }

        [HttpGet]
        [HasPermission(PermissionModule.CourseBatchManagement, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<DepartmentDto>>> List(
            [FromQuery] bool includeInactive,
            CancellationToken cancellationToken)
        {
            return Ok(await _departmentService.ListAsync(includeInactive, cancellationToken));
        }

        [HttpPost]
        [HasPermission(PermissionModule.CourseBatchManagement, PermissionAction.Create)]
        public async Task<ActionResult<DepartmentDto>> Create(
            SaveDepartmentRequest request,
            CancellationToken cancellationToken)
        {
            var department = await _departmentService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(List), null, department);
        }

        [HttpPut("{id:guid}")]
        [HasPermission(PermissionModule.CourseBatchManagement, PermissionAction.Edit)]
        public async Task<ActionResult<DepartmentDto>> Update(
            Guid id,
            SaveDepartmentRequest request,
            CancellationToken cancellationToken)
        {
            return Ok(await _departmentService.UpdateAsync(id, request, cancellationToken));
        }
    }
}
