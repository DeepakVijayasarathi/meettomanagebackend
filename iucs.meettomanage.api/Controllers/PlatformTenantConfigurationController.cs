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

    /// <summary>One bounded, tenant-scoped payload for the operator's health/usage/onboarding workspace.</summary>
    [HttpGet("operations")]
    public async Task<IActionResult> Operations(Guid tenantId, CancellationToken ct)
    {
        var tenant = await master.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new NotFoundException("Tenant", tenantId);
        var subscription = await master.TenantSubscriptions.AsNoTracking().Include(x => x.Plan)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var snapshot = await master.TenantUsageSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.SnapshotDate).FirstOrDefaultAsync(ct);
        var business = await InTenant(tenantId, async sp =>
        {
            var db = sp.GetRequiredService<MeetToManageDbContext>();
            var settings = await db.AppSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, ct);
            var recentAudit = await db.AuditLogs.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Take(12)
                .Select(x => new { action = x.Action.ToString(), x.EntityName, x.EntityId, x.CreatedAtUtc }).ToListAsync(ct);
            return new
            {
                users = await db.Users.CountAsync(x => x.Status == domain.Enums.UserStatus.Active, ct), students = await db.Children.CountAsync(ct),
                courses = await db.Courses.CountAsync(ct), batches = await db.Batches.CountAsync(ct),
                sessions = await db.ClassSessions.CountAsync(ct), resources = await db.Resources.CountAsync(ct),
                hasBranding = settings.Any(x => (x.Key == "brand.name" || x.Key == "brand.logoUrl") && !string.IsNullOrWhiteSpace(x.Value)),
                hasOrganization = settings.TryGetValue("org.name", out var organizationName) && !string.IsNullOrWhiteSpace(organizationName),
                hasIntegrations = await db.Integrations.AnyAsync(x => x.IsEnabled, ct), recentAudit,
            };
        }, ct);
        return Ok(new
        {
            tenant = new { tenant.Id, tenant.Code, tenant.Name, tenant.CustomDomain, tenant.CustomDomainVerifiedAtUtc, status = tenant.Status.ToString(), tenant.AppAccessGranted },
            subscription = subscription is null ? null : new { plan = subscription.Plan.Name, status = subscription.Status.ToString(), subscription.TrialEndsAtUtc, subscription.CurrentPeriodEndUtc },
            usage = snapshot is null
                ? new { students = business.students, staff = business.users, asOf = (DateOnly?)null }
                : new { students = snapshot.ActiveStudents, staff = snapshot.StaffCount, asOf = (DateOnly?)snapshot.SnapshotDate },
            business,
            onboarding = new
            {
                provisioned = tenant.ProvisionedAtUtc is not null,
                organization = business.hasOrganization,
                branding = business.hasBranding,
                firstUser = business.users > 0,
                firstCourse = business.courses > 0,
                firstBatch = business.batches > 0,
            }
        });
    }

    [HttpPut("configuration")]
    public async Task<IActionResult> SaveConfiguration(Guid tenantId, List<UpdateSettingRequest> updates, CancellationToken ct) =>
        Ok(await InTenant(tenantId, sp => sp.GetRequiredService<ISettingsService>().UpsertAsync(updates, ct), ct));

    /// <summary>Aggregate, privacy-safe operational status for the selected tenant's family app.</summary>
    [HttpGet("app-status")]
    public async Task<IActionResult> AppStatus(Guid tenantId, CancellationToken ct, [FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 7, 90);
        var tenant = await master.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new NotFoundException("Tenant", tenantId);
        var result = await InTenant(tenantId, async sp =>
        {
            var db = sp.GetRequiredService<MeetToManageDbContext>();
            var now = DateTime.UtcNow;
            var since = now.AddDays(-days);
            var rows = await db.MobileAppInstallations.AsNoTracking().Where(x => x.LastSeenAtUtc >= since)
                .Select(x => new { x.UserId, x.Platform, x.AppVersion, x.BuildNumber, x.NotificationsEnabled, x.BiometricLockEnabled, x.LastSeenAtUtc, x.LastSuccessfulSyncAtUtc, x.LastErrorCategory })
                .ToListAsync(ct);
            var settings = await db.AppSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, ct);
            bool Has(string key) => settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
            var eligible = await db.Users.CountAsync(x => x.Status == domain.Enums.UserStatus.Active && x.Role == domain.Enums.UserRole.Parent, ct);
            var latestVersion = rows.GroupBy(x => x.AppVersion).OrderByDescending(g => ParseVersion(g.Key)).ThenByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            var active30Since = now.AddDays(-30);
            var recent30 = rows.Where(x => x.LastSeenAtUtc >= active30Since).ToList();
            var trend = Enumerable.Range(0, days).Select(offset =>
            {
                var date = DateOnly.FromDateTime(now.Date.AddDays(-(days - 1 - offset)));
                var count = rows.Where(x => DateOnly.FromDateTime(x.LastSeenAtUtc) == date).Select(x => x.UserId).Distinct().Count();
                return new { date, activeFamilies = count };
            });
            return new
            {
                generatedAtUtc = now,
                windowDays = days,
                health = !tenant.AppAccessGranted ? "disabled" : rows.Count == 0 ? "no-data" : rows.Max(x => x.LastSeenAtUtc) < now.AddHours(-24) ? "stale" : "operational",
                accessGranted = tenant.AppAccessGranted,
                eligibleFamilies = eligible,
                active = new
                {
                    installations24h = rows.Count(x => x.LastSeenAtUtc >= now.AddHours(-24)),
                    installations7d = rows.Count(x => x.LastSeenAtUtc >= now.AddDays(-7)),
                    installations30d = recent30.Count,
                    families30d = recent30.Select(x => x.UserId).Distinct().Count(),
                    lastSeenAtUtc = rows.Count == 0 ? (DateTime?)null : rows.Max(x => x.LastSeenAtUtc),
                    lastSuccessfulSyncAtUtc = rows.Where(x => x.LastSuccessfulSyncAtUtc != null).Select(x => x.LastSuccessfulSyncAtUtc).Max(),
                },
                adoption = new
                {
                    latestVersion,
                    latestVersionPercent = recent30.Count == 0 || latestVersion is null ? 0 : Math.Round(recent30.Count(x => x.AppVersion == latestVersion) * 100d / recent30.Count, 1),
                    notificationOptInPercent = recent30.Count == 0 ? 0 : Math.Round(recent30.Count(x => x.NotificationsEnabled) * 100d / recent30.Count, 1),
                    biometricOptInPercent = recent30.Count == 0 ? 0 : Math.Round(recent30.Count(x => x.BiometricLockEnabled) * 100d / recent30.Count, 1),
                },
                platforms = recent30.GroupBy(x => x.Platform).Select(g => new { platform = g.Key, installations = g.Count() }).OrderByDescending(x => x.installations),
                versions = recent30.GroupBy(x => new { x.AppVersion, x.Platform }).Select(g => new { version = g.Key.AppVersion, platform = g.Key.Platform, installations = g.Count(), lastSeenAtUtc = g.Max(x => x.LastSeenAtUtc) }).OrderByDescending(x => x.installations),
                errors = recent30.Where(x => x.LastErrorCategory != null).GroupBy(x => x.LastErrorCategory!).Select(g => new { category = g.Key, installations = g.Count(), lastSeenAtUtc = g.Max(x => x.LastSeenAtUtc) }).OrderByDescending(x => x.installations),
                trend,
                readiness = new
                {
                    access = tenant.AppAccessGranted,
                    branding = Has("brand.name") || Has("brand.logoUrl"),
                    portal = Has("app.webBaseUrl") || !string.IsNullOrWhiteSpace(configuration["Frontend:BaseUrl"]),
                    support = Has("org.supportEmail") || Has("org.supportPhone"),
                    welcome = settings.TryGetValue("app.welcome.enabled", out var welcome) && welcome == "false" || Has("app.welcome.title") || Has("app.welcome.features"),
                },
            };
        }, ct);
        return Ok(result);
    }

    private static Version ParseVersion(string value) => Version.TryParse(value, out var version) ? version : new Version(0, 0);

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
