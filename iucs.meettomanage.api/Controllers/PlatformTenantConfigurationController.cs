using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Dto.Navigation;
using iucs.meettomanage.application.Dto.Settings;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Controllers;

/// <summary>Operator access to one explicitly selected client's configuration.</summary>
[ApiController, Route("api/platform/tenants/{tenantId:guid}"), Authorize(Policy = "PlatformAdmin")]
public sealed class PlatformTenantConfigurationController(MasterDbContext master, TenantScopeFactory scopes) : ControllerBase
{
    private async Task<T> InTenant<T>(Guid id, Func<IServiceProvider, Task<T>> operation, CancellationToken ct)
    {
        var tenant = await master.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Tenant", id);
        if (tenant.Status is not (TenantStatus.Active or TenantStatus.Suspended))
            throw new ConflictException("Complete tenant provisioning before managing its configuration.");
        using var scope = scopes.CreateScope(tenant);
        using var ambient = TenantExecution.Enter(tenant.Id, tenant.Code);
        return await operation(scope.ServiceProvider);
    }

    [HttpGet("configuration")]
    public async Task<IActionResult> Configuration(Guid tenantId, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<ISettingsService>().GetAllAsync(ct), ct));

    [HttpPut("configuration")]
    public async Task<IActionResult> SaveConfiguration(Guid tenantId, List<UpdateSettingRequest> updates, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<ISettingsService>().UpsertAsync(updates, ct), ct));

    [HttpGet("menus")]
    public async Task<IActionResult> Menus(Guid tenantId, [FromQuery] string? portal, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<IMenuService>().ListAsync(portal, ct), ct));

    [HttpPost("menus")]
    public async Task<IActionResult> CreateMenu(Guid tenantId, SaveMenuItemRequest request, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<IMenuService>().CreateAsync(request, ct), ct));

    [HttpPut("menus/{menuId:guid}")]
    public async Task<IActionResult> UpdateMenu(Guid tenantId, Guid menuId, SaveMenuItemRequest request, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<IMenuService>().UpdateAsync(menuId, request, ct), ct));

    [HttpDelete("menus/{menuId:guid}")]
    public async Task<IActionResult> DeleteMenu(Guid tenantId, Guid menuId, CancellationToken ct)
    {
        await InTenant(tenantId, async sp => { await sp.GetRequiredService<IMenuService>().DeleteAsync(menuId, ct); return true; }, ct);
        return NoContent();
    }

    [HttpGet("permission-modules")]
    public async Task<IActionResult> PermissionModules(Guid tenantId, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<IPermissionModuleService>().ListAsync(ct), ct));
}
