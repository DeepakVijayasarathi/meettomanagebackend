using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace iucs.meettomanage.api.Saas;

/// <summary>
/// Central enforcement of the student and staff limits: any save that would add students or staff
/// beyond the tenant's plan is rejected here, whichever controller or import path triggered it.
/// Only live HTTP requests are checked (not workers, seeding or provisioning), and the check fails
/// open if the master database is unreachable so a billing outage never stops teaching.
/// </summary>
public sealed class EntitlementInterceptor(IEntitlementService entitlements, TenantContext tenant,
    IHttpContextAccessor http, ILogger<EntitlementInterceptor> logger) : SaveChangesInterceptor
{
    private static readonly UserRole[] StaffRoles = [UserRole.Admin, UserRole.SubAdmin, UserRole.AdmissionTeam, UserRole.Teacher];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not MeetToManageDbContext db || tenant.TenantId is not { } tenantId) return result;
        if (http.HttpContext is not { } context || context.User.IsInRole("PlatformAdmin")) return result;

        var newStudents = db.ChangeTracker.Entries<Child>().Count(x => x.State == EntityState.Added && x.Entity.IsActive);
        var newStaff = db.ChangeTracker.Entries<User>().Count(x => x.State == EntityState.Added
            && x.Entity.Status != UserStatus.Inactive && StaffRoles.Contains(x.Entity.Role));
        if (newStudents == 0 && newStaff == 0) return result;

        TenantEntitlements granted;
        try { granted = await entitlements.GetAsync(tenantId, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Plan limits could not be read for tenant {TenantId}; allowing the save.", tenantId);
            return result;
        }
        if (!granted.Managed) return result;

        if (newStudents > 0)
        {
            var limit = granted.Limit(EntitlementKeys.MaxStudents);
            if (limit != EntitlementKeys.Unlimited)
            {
                var current = await db.Children.CountAsync(x => x.IsActive, cancellationToken);
                if (!SaasRules.WithinLimit(limit, current, newStudents))
                    throw new PlanLimitException($"Your {granted.PlanName} plan allows up to {limit} active students. Upgrade your plan to add more.");
            }
        }
        if (newStaff > 0)
        {
            var limit = granted.Limit(EntitlementKeys.MaxStaff);
            if (limit != EntitlementKeys.Unlimited)
            {
                var current = await db.Users.CountAsync(x => StaffRoles.Contains(x.Role) && x.Status != UserStatus.Inactive, cancellationToken);
                if (!SaasRules.WithinLimit(limit, current, newStaff))
                    throw new PlanLimitException($"Your {granted.PlanName} plan allows up to {limit} staff and teacher accounts. Upgrade your plan to add more.");
            }
        }
        return result;
    }
}

/// <summary>Rejects the request with 402 unless the tenant's plan includes the named feature.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresFeatureAttribute(string feature) : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        if (services.GetService<TenantContext>()?.TenantId is { } tenantId)
        {
            var granted = await services.GetRequiredService<IEntitlementService>().GetAsync(tenantId, context.HttpContext.RequestAborted);
            if (!granted.Can(feature))
                throw new PlanLimitException($"This feature is not included in your {granted.PlanName} plan. Upgrade to use it.");
        }
        await next();
    }
}
