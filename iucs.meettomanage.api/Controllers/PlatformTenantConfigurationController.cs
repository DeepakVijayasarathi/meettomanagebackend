using iucs.meettomanage.api.Services;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Integrations;
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
public sealed class PlatformTenantConfigurationController(MasterDbContext master, TenantScopeFactory scopes, IConfiguration configuration) : ControllerBase
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

    // seo.* settings are hidden from the normal settings list on purpose (the public site's SEO
    // belongs to the marketing scope), so the operator reads and writes them through the same
    // marketing-scope service methods.
    [HttpGet("seo")]
    public async Task<IActionResult> Seo(Guid tenantId, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<ISettingsService>().GetMarketingAsync(ct), ct));

    [HttpPut("seo")]
    public async Task<IActionResult> SaveSeo(Guid tenantId, List<UpdateSettingRequest> updates, CancellationToken ct) =>
        Ok(await InTenant(tenantId, async sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            await settings.UpsertMarketingAsync(updates, ct);
            return await settings.GetMarketingAsync(ct);
        }, ct));

    [HttpGet("integrations")]
    public async Task<IActionResult> Integrations(Guid tenantId, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<IIntegrationService>().ListAsync(ct), ct));

    [HttpPut("integrations/{integrationId:guid}")]
    public async Task<IActionResult> UpdateIntegration(Guid tenantId, Guid integrationId, SaveIntegrationRequest request, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<IIntegrationService>().UpdateAsync(integrationId, request, ct), ct));

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

    /// <summary>Uploads the selected client's logo (stored in that client's own file storage).</summary>
    [HttpPost("branding/logo"), RequestSizeLimit(BrandLogo.MaxBytes + 4096)]
    public async Task<IActionResult> UploadLogo(Guid tenantId, IFormFile file, CancellationToken ct) =>
        Ok(await InTenant(tenantId, async sp =>
        {
            await using var stream = file.OpenReadStream();
            var url = await BrandLogo.SaveAsync(sp.GetRequiredService<IFileStorage>(), sp.GetRequiredService<ISettingsService>(),
                stream, file.FileName, BrandLogo.PublicBaseUrl(configuration, Request), ct);
            return new { logoUrl = url };
        }, ct));

    [HttpDelete("branding/logo")]
    public async Task<IActionResult> RemoveLogo(Guid tenantId, CancellationToken ct)
    {
        await InTenant(tenantId, async sp => { await BrandLogo.ClearAsync(sp.GetRequiredService<ISettingsService>(), ct); return true; }, ct);
        return NoContent();
    }

    [HttpGet("permission-modules")]
    public async Task<IActionResult> PermissionModules(Guid tenantId, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<IPermissionModuleService>().ListAsync(ct), ct));
}
