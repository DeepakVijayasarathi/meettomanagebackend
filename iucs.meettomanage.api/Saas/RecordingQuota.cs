using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.domain.Data;

namespace iucs.meettomanage.api.Saas;

/// <summary>
/// Whether the academy's plan has room for one more class recording (monthly count and storage).
/// The save itself is checked too (EntitlementInterceptor), but a browser recording uploads for the
/// whole class first: asking up front stops a class being uploaded only to be refused at the end,
/// and lets an interrupted upload wait (parts kept) until the plan has room again.
/// </summary>
public sealed class RecordingQuota(IServiceProvider services, MeetToManageDbContext db)
{
    /// <summary>Why a new recording would be refused, or null when there's room (or no plan limits apply).</summary>
    public async Task<string?> RefusalAsync(CancellationToken cancellationToken = default)
    {
        var entitlements = services.GetService<IEntitlementService>();
        var tenantId = TenantExecution.Current?.Id ?? services.GetService<TenantContext>()?.TenantId;
        if (entitlements is null || tenantId is null) return null;

        var granted = await entitlements.GetAsync(tenantId.Value, cancellationToken);
        var maxRecordings = granted.Limit(EntitlementKeys.MaxRecordingsPerMonth);
        var maxStorageMb = granted.Limit(EntitlementKeys.MaxStorageMb);
        if (maxRecordings == EntitlementKeys.Unlimited && maxStorageMb == EntitlementKeys.Unlimited) return null;

        var usage = await SaasUsageService.ComputeAsync(db, DateTime.UtcNow, cancellationToken);
        if (maxRecordings != EntitlementKeys.Unlimited && !SaasRules.WithinLimit(maxRecordings, usage.RecordingsMonth, 1))
            return $"Your {granted.PlanName} plan allows up to {maxRecordings} recordings per month.";
        if (maxStorageMb != EntitlementKeys.Unlimited && usage.StorageMb >= maxStorageMb)
            return $"Your {granted.PlanName} plan's {maxStorageMb} MB of storage is full.";
        return null;
    }
}
