using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Entities.Resources;
using iucs.meettomanage.domain.Entities.Sessions;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace iucs.meettomanage.api.Saas;

/// <summary>
/// Central enforcement of count and storage limits: any save that would add metered records
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
        var newSessions = db.ChangeTracker.Entries<ClassSession>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToList();
        var startingSessions = db.ChangeTracker.Entries<ClassSession>().Where(x => x.State == EntityState.Modified
            && x.Property(nameof(ClassSession.Status)).IsModified
            && x.Entity.Status == SessionStatus.InProgress).Select(x => x.Entity).ToList();
        var newSessionRecordingEntries = db.ChangeTracker.Entries<SessionRecording>().Where(x => x.State == EntityState.Added).ToList();
        var newSessionRecordings = newSessionRecordingEntries.Count;
        var newPersonalRecordings = db.ChangeTracker.Entries<PersonalRoomRecording>().Count(x => x.State == EntityState.Added);
        var addedStorageBytes = db.ChangeTracker.Entries<Resource>().Where(x => x.State == EntityState.Added)
            .Sum(x => Math.Max(0, x.Entity.FileSizeBytes ?? 0));
        if (newStudents == 0 && newStaff == 0 && newSessions.Count == 0 && startingSessions.Count == 0
            && newSessionRecordings == 0 && newPersonalRecordings == 0 && addedStorageBytes == 0) return result;

        TenantEntitlements granted;
        try { granted = await entitlements.GetAsync(tenantId, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Plan limits could not be read for tenant {TenantId}; allowing the save.", tenantId);
            return result;
        }
        if (!granted.Managed) return result;

        if (newSessionRecordingEntries.Count > 0)
        {
            var retentionDays = granted.Limit(EntitlementKeys.RecordingRetentionDays);
            foreach (var entry in newSessionRecordingEntries)
                entry.Entity.ExpiresAtUtc = retentionDays == EntitlementKeys.Unlimited
                    ? null
                    : DateTime.UtcNow.AddDays(Math.Max(0, retentionDays));
        }

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
        if (newSessions.Count > 0)
        {
            var limit = granted.Limit(EntitlementKeys.MaxSessionsPerMonth);
            if (limit != EntitlementKeys.Unlimited)
            {
                foreach (var group in newSessions.GroupBy(x => new { x.ScheduledStartAtUtc.Year, x.ScheduledStartAtUtc.Month }))
                {
                    var start = new DateTime(group.Key.Year, group.Key.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                    var end = start.AddMonths(1);
                    var current = await db.ClassSessions.CountAsync(x => x.ScheduledStartAtUtc >= start
                        && x.ScheduledStartAtUtc < end, cancellationToken);
                    if (!SaasRules.WithinLimit(limit, current, group.Count()))
                        throw new PlanLimitException($"Your {granted.PlanName} plan allows up to {limit} sessions per month. Increase the tenant limit or upgrade the plan.");
                }
            }
        }
        if (startingSessions.Count > 0)
        {
            var limit = granted.Limit(EntitlementKeys.MaxSessionMinutesPerMonth);
            if (limit != EntitlementKeys.Unlimited)
            {
                var now = DateTime.UtcNow;
                var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var completed = await db.ClassSessions.AsNoTracking()
                    .Where(x => x.ScheduledStartAtUtc >= start && x.ScheduledStartAtUtc <= now
                        && x.Status == SessionStatus.Completed)
                    .Select(x => new { Start = x.ActualStartAtUtc ?? x.ScheduledStartAtUtc, End = x.ActualEndAtUtc ?? x.ScheduledEndAtUtc })
                    .ToListAsync(cancellationToken);
                var usedMinutes = (long)completed.Sum(x => Math.Max(0, (x.End - x.Start).TotalMinutes));
                if (usedMinutes >= limit)
                    throw new PlanLimitException($"Your {granted.PlanName} plan's {limit}-minute monthly live-session allowance has been used. Increase the tenant limit or upgrade the plan.");
            }
        }
        var newRecordings = newSessionRecordings + newPersonalRecordings;
        if (newRecordings > 0)
        {
            var limit = granted.Limit(EntitlementKeys.MaxRecordingsPerMonth);
            if (limit != EntitlementKeys.Unlimited)
            {
                var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var current = await db.SessionRecordings.CountAsync(x => x.CreatedAtUtc >= start, cancellationToken)
                    + await db.PersonalRoomRecordings.CountAsync(x => x.CreatedAtUtc >= start, cancellationToken);
                if (!SaasRules.WithinLimit(limit, current, newRecordings))
                    throw new PlanLimitException($"Your {granted.PlanName} plan allows up to {limit} recordings per month. Increase the tenant limit or upgrade the plan.");
            }
        }
        if (addedStorageBytes > 0)
        {
            var limitMb = granted.Limit(EntitlementKeys.MaxStorageMb);
            if (limitMb != EntitlementKeys.Unlimited)
            {
                var currentBytes = await db.Resources.SumAsync(x => x.FileSizeBytes ?? 0L, cancellationToken);
                var limitBytes = limitMb > long.MaxValue / (1024L * 1024L) ? long.MaxValue : limitMb * 1024L * 1024L;
                if (currentBytes > limitBytes - addedStorageBytes)
                    throw new PlanLimitException($"Your {granted.PlanName} plan includes {limitMb} MB of file storage. Remove files, increase the tenant limit, or upgrade the plan.");
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
