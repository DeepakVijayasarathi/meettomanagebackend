using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Dto.Users;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>
    /// DB-maintained permission roles (presets). Applying a role to a Sub Admin
    /// goes through PUT /api/users/{id}/permissions/preset/{name}.
    /// </summary>
    [ApiController]
    [Route("api/roles")]
    public class RolesController : ControllerBase
    {
        private readonly IRoleService _roleService;

        public RolesController(IRoleService roleService)
        {
            _roleService = roleService;
        }

        // Gated on Settings, not UserManagement: a RoleDefinition IS the permission
        // matrix other Sub Admins run on, so it's platform configuration (same category
        // as Settings/Integrations/Menus) rather than routine user-record management —
        // a Sub Admin whose job is "manage user records" must not also be able to grant
        // or edit any role's permission matrix, including one already assigned to someone
        // else, through the same claim.
        [HttpGet]
        [HasPermission(PermissionModule.Settings, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<RoleDto>>> List(CancellationToken cancellationToken)
        {
            return Ok(await _roleService.ListAsync(cancellationToken));
        }

        [HttpPost]
        [HasPermission(PermissionModule.Settings, PermissionAction.Edit)]
        public async Task<ActionResult<RoleDto>> Create(SaveRoleRequest request, CancellationToken cancellationToken)
        {
            var role = await _roleService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(List), null, role);
        }

        [HttpPut("{id:guid}")]
        [HasPermission(PermissionModule.Settings, PermissionAction.Edit)]
        public async Task<ActionResult<RoleDto>> Update(Guid id, SaveRoleRequest request, CancellationToken cancellationToken)
        {
            return Ok(await _roleService.UpdateAsync(id, request, cancellationToken));
        }

        [HttpDelete("{id:guid}")]
        [HasPermission(PermissionModule.Settings, PermissionAction.Edit)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        {
            await _roleService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
    }
}
