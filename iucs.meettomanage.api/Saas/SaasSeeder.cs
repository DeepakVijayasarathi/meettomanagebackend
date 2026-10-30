using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Saas;

/// <summary>
/// Idempotent start-up data for the SaaS layer: the starter plan catalogue (only when the catalogue is empty, so
/// operator edits are never overwritten), the hidden "legacy" plan, the billing e-mail templates, and a "legacy"
/// subscription for every tenant that existed before billing was introduced so their behaviour does not change.
/// The seeded prices are placeholders for the operator to edit in the platform console.
/// </summary>
public static class SaasSeeder
{
    public const string LegacyPlanCode = "legacy";

    public static async Task SeedAsync(IDbContextFactory<MasterDbContext> factory, IOptions<SaasOptions> options, ILogger logger, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        if (!await db.Plans.AnyAsync(ct))
        {
            db.Plans.AddRange(
                NewPlan("trial", "Free Trial", "Try everything for a limited time.", false, options.Value.DefaultTrialDays, 0,
                    new Limits(25, 5, 2 * 1024, 600, 100, 100, 7), Features(recordings: true, whiteLabel: false, gateway: false), []),
                NewPlan("starter", "Starter", "For a single academy getting started.", true, 0, 1,
                    new Limits(100, 15, 20 * 1024, 6_000, 1_000, 1_000, 16), Features(recordings: true, whiteLabel: true, gateway: true),
                    [(BillingInterval.Month, 2999m), (BillingInterval.Year, 29990m)]),
                NewPlan("growth", "Growth", "For growing academies with more learners and automation.", true, 0, 2,
                    new Limits(500, 50, 200 * 1024, 30_000, 5_000, 5_000, 30), Features(recordings: true, whiteLabel: true, gateway: true, whatsapp: true, api: true),
                    [(BillingInterval.Month, 7999m), (BillingInterval.Year, 79990m)]),
                NewPlan("enterprise", "Enterprise", "Custom limits, custom domain and dedicated support. Contact sales.", false, 0, 3,
                    new Limits(-1, -1, -1, -1, -1, -1, -1), Features(true, true, true, true, true, true), []));
        }
        if (!await db.Plans.AnyAsync(x => x.Code == LegacyPlanCode, ct))
        {
            db.Plans.Add(NewPlan(LegacyPlanCode, "Legacy (unlimited)", "Clients that existed before billing. No limits, never renews.", false, 0, 99,
                new Limits(-1, -1, -1, -1, -1, -1, -1), Features(true, true, true, true, true, true), []));
        }
        await db.SaveChangesAsync(ct);

        // Plans created before session/recording metering was introduced do not have these
        // catalogue rows. Backfill only missing values: operator-edited limits are never
        // overwritten, and custom plans remain unlimited until the operator chooses a value.
        var existingPlans = await db.Plans.Include(x => x.Entitlements).ToListAsync(ct);
        foreach (var plan in existingPlans)
        {
            AddMissingLimit(plan, EntitlementKeys.MaxSessionsPerMonth, DefaultUsageLimit(plan.Code));
            AddMissingLimit(plan, EntitlementKeys.MaxRecordingsPerMonth, DefaultUsageLimit(plan.Code));
        }
        await db.SaveChangesAsync(ct);

        var present = await db.PlatformEmailTemplates.Select(x => x.Key).ToListAsync(ct);
        foreach (var template in DefaultEmailTemplates.All.Where(x => !present.Contains(x.Key)))
            db.PlatformEmailTemplates.Add(new PlatformEmailTemplate { Key = template.Key, Subject = template.Subject, Body = template.Body });
        await db.SaveChangesAsync(ct);

        var legacy = await db.Plans.SingleAsync(x => x.Code == LegacyPlanCode, ct);
        var unmanaged = await db.Tenants.Where(x => (x.Status == TenantStatus.Active || x.Status == TenantStatus.Suspended)
            && !db.TenantSubscriptions.Any(s => s.TenantId == x.Id)).ToListAsync(ct);
        foreach (var tenant in unmanaged)
        {
            var sub = new TenantSubscription { TenantId = tenant.Id, PlanId = legacy.Id, Status = SaasSubscriptionStatus.Active };
            db.TenantSubscriptions.Add(sub);
            db.TenantSubscriptionEvents.Add(new TenantSubscriptionEvent
            {
                TenantId = tenant.Id, SubscriptionId = sub.Id, EventType = "subscription_created", Detail = "Existing tenant moved to the legacy plan", Actor = "system",
            });
        }
        await db.SaveChangesAsync(ct);
        if (unmanaged.Count > 0) logger.LogInformation("{Count} existing tenant(s) were placed on the legacy plan.", unmanaged.Count);
    }

    private sealed record Limits(long Students, long Staff, long StorageMb, long SessionMinutes,
        long Sessions, long Recordings, long RetentionDays);

    private static long DefaultUsageLimit(string planCode) => planCode switch
    {
        "trial" => 100,
        "starter" => 1_000,
        "growth" => 5_000,
        _ => EntitlementKeys.Unlimited,
    };

    private static void AddMissingLimit(Plan plan, string key, long value)
    {
        if (plan.Entitlements.Any(x => x.Key == key)) return;
        plan.Entitlements.Add(new PlanEntitlement
        {
            PlanId = plan.Id,
            Key = key,
            ValueType = EntitlementValueType.Number,
            Value = value,
        });
    }

    private static Dictionary<string, long> Features(bool recordings = false, bool whiteLabel = false, bool gateway = false,
        bool whatsapp = false, bool api = false, bool customDomain = false) => new()
    {
        [EntitlementKeys.Recordings] = recordings ? 1 : 0,
        [EntitlementKeys.WhiteLabel] = whiteLabel ? 1 : 0,
        [EntitlementKeys.PaymentGateway] = gateway ? 1 : 0,
        [EntitlementKeys.Whatsapp] = whatsapp ? 1 : 0,
        [EntitlementKeys.ApiAccess] = api ? 1 : 0,
        [EntitlementKeys.CustomDomain] = customDomain ? 1 : 0,
    };

    private static Plan NewPlan(string code, string name, string description, bool isPublic, int trialDays, int order, Limits limits,
        Dictionary<string, long> features, (BillingInterval Interval, decimal Amount)[] prices)
    {
        var plan = new Plan { Code = code, Name = name, Description = description, IsPublic = isPublic, TrialDays = trialDays, SortOrder = order };
        void Number(string key, long value) =>
            plan.Entitlements.Add(new PlanEntitlement { Key = key, ValueType = EntitlementValueType.Number, Value = value });
        Number(EntitlementKeys.MaxStudents, limits.Students);
        Number(EntitlementKeys.MaxStaff, limits.Staff);
        Number(EntitlementKeys.MaxStorageMb, limits.StorageMb);
        Number(EntitlementKeys.MaxSessionMinutesPerMonth, limits.SessionMinutes);
        Number(EntitlementKeys.MaxSessionsPerMonth, limits.Sessions);
        Number(EntitlementKeys.MaxRecordingsPerMonth, limits.Recordings);
        Number(EntitlementKeys.RecordingRetentionDays, limits.RetentionDays);
        foreach (var (key, value) in features)
            plan.Entitlements.Add(new PlanEntitlement { Key = key, ValueType = EntitlementValueType.Bool, Value = value });
        foreach (var (interval, amount) in prices)
            plan.Prices.Add(new PlanPrice { BillingInterval = interval, Amount = amount });
        return plan;
    }
}
