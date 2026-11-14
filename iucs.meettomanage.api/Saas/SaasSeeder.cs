using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using iucs.meettomanage.domain.Enums;
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
    public const string FreePlanCode = "free";

    /// <summary>Core modules the Free plan includes; the operator can change this in the platform console.</summary>
    public static readonly IReadOnlySet<PermissionModule> FreeModules = new HashSet<PermissionModule>
    {
        PermissionModule.UserManagement, PermissionModule.CourseBatchManagement, PermissionModule.SessionCalendarManagement,
        PermissionModule.ContentAccessManagement, PermissionModule.Communication, PermissionModule.Settings, PermissionModule.SupportTickets,
    };

    public static async Task SeedAsync(IDbContextFactory<MasterDbContext> factory, IOptions<SaasOptions> options, ILogger logger, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        if (!await db.Plans.AnyAsync(ct))
        {
            db.Plans.AddRange(
                NewPlan("trial", "Free Trial", "Try everything for a limited time.", false, options.Value.DefaultTrialDays, 0,
                    new Limits(25, 5, 2 * 1024, 600, 100, 100, 7), Features(recordings: true, whiteLabel: true, gateway: true, whatsapp: true), []),
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
        if (!await db.Plans.AnyAsync(x => x.Code == FreePlanCode, ct))
        {
            var free = NewPlan(FreePlanCode, "Free", "Free forever for small academies: students, courses, batches, live classes and content.", true, 0, 0,
                new Limits(20, 3, 1024, 1_200, 60, 0, 0), Features(), [(BillingInterval.Month, 0m)]);
            foreach (var module in Enum.GetValues<PermissionModule>())
                free.Entitlements.Add(new PlanEntitlement
                {
                    Key = EntitlementKeys.ModuleKey(module), ValueType = EntitlementValueType.Bool, Value = FreeModules.Contains(module) ? 1 : 0,
                });
            db.Plans.Add(free);
            logger.LogInformation("Seeded the public Free plan.");
        }
        await db.SaveChangesAsync(ct);

        // The trial was first seeded with branding, gateway and WhatsApp off, back when no feature flag was enforced.
        // Now that they are, a trial must still show everything it promises; only an untouched trial plan is changed.
        var trialPlan = await db.Plans.Include(x => x.Entitlements).SingleOrDefaultAsync(x => x.Code == "trial", ct);
        if (trialPlan is not null)
        {
            long Flag(string key) => trialPlan.Entitlements.FirstOrDefault(x => x.Key == key)?.Value ?? -1;
            var asSeeded = Flag(EntitlementKeys.WhiteLabel) == 0 && Flag(EntitlementKeys.PaymentGateway) == 0
                && Flag(EntitlementKeys.Whatsapp) == 0 && Flag(EntitlementKeys.Recordings) == 1;
            if (asSeeded)
                foreach (var row in trialPlan.Entitlements.Where(x => x.Key is EntitlementKeys.WhiteLabel or EntitlementKeys.PaymentGateway or EntitlementKeys.Whatsapp))
                    row.Value = 1;
            await db.SaveChangesAsync(ct);
        }

        await ApplyNovember2026PricingAsync(db, logger, ct);

        // Plans created before session/recording metering was introduced do not have these
        // catalogue rows. Backfill only missing values: operator-edited limits are never
        // overwritten, and custom plans remain unlimited until the operator chooses a value.
        var existingPlans = await db.Plans.Include(x => x.Entitlements).ToListAsync(ct);
        foreach (var plan in existingPlans)
        {
            AddMissingLimit(plan, EntitlementKeys.MaxSessionsPerMonth, DefaultUsageLimit(plan.Code));
            AddMissingLimit(plan, EntitlementKeys.MaxRecordingsPerMonth, DefaultUsageLimit(plan.Code));
            // Plans created before module gating include every module, as they always did.
            foreach (var module in EntitlementKeys.Modules)
                AddMissingFlag(db, plan, module.Key, 1);
        }
        await db.SaveChangesAsync(ct);

        var present = await db.PlatformEmailTemplates.Select(x => x.Key).ToListAsync(ct);
        foreach (var template in DefaultEmailTemplates.All.Where(x => !present.Contains(x.Key)))
            db.PlatformEmailTemplates.Add(new PlatformEmailTemplate { Key = template.Key, Subject = template.Subject, Body = template.Body });
        await db.SaveChangesAsync(ct);

        // Trial emails were reworded when trials began falling back to Free; upgrade only copies the operator never edited.
        var reworded = DefaultEmailTemplates.PreviousBodies.Keys.ToArray();
        foreach (var stored in await db.PlatformEmailTemplates.Where(x => reworded.Contains(x.Key)).ToListAsync(ct))
        {
            if (DefaultEmailTemplates.PreviousBodies[stored.Key] != stored.Body) continue;
            stored.Body = DefaultEmailTemplates.All.Single(x => x.Key == stored.Key).Body;
        }
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

    /// <summary>
    /// The November 2026 price list: Free, Basic ₹999, Pro ₹2,499, Growth ₹5,999 (yearly = 10 months).
    /// Runs on every start but changes only what is still as originally seeded, so an operator's edits
    /// in the platform console always win. Existing subscribers keep the price row they bought
    /// (old prices are deactivated, not changed), and Starter subscribers stay on Starter; it is only
    /// hidden from new buyers.
    /// </summary>
    private static async Task ApplyNovember2026PricingAsync(MasterDbContext db, ILogger logger, CancellationToken ct)
    {
        var plans = await db.Plans.Include(x => x.Entitlements).Include(x => x.Prices).ToListAsync(ct);
        Plan? Find(string code) => plans.FirstOrDefault(x => x.Code == code);
        static bool StillPriced(Plan plan, decimal monthly) => plan.Prices.Any(p => p.IsActive && p.Currency == "INR"
            && p.BillingInterval == BillingInterval.Month && p.Amount == monthly);
        static void Raise(Plan plan, string key, long from, long to)
        {
            var row = plan.Entitlements.FirstOrDefault(x => x.Key == key);
            if (row is not null && row.Value == from) row.Value = to;
        }
        static void Flag(MasterDbContext db, Plan plan, string key, long value)
        {
            if (plan.Entitlements.Any(x => x.Key == key)) return;
            db.PlanEntitlements.Add(new PlanEntitlement { PlanId = plan.Id, Key = key, ValueType = EntitlementValueType.Bool, Value = value });
        }
        var changed = false;

        if (Find("basic") is null && Find("pro") is null)
        {
            var basic = NewPlan("basic", "Basic", "For tuition centres and small academies starting online.", true, 14, 1,
                new Limits(75, 5, 10 * 1024, 3_600, 300, 300, 7), Features(recordings: true, whiteLabel: true, gateway: true),
                [(BillingInterval.Month, 999m), (BillingInterval.Year, 9_990m)]);
            basic.Entitlements.Add(new PlanEntitlement { Key = EntitlementKeys.ServerRecording, ValueType = EntitlementValueType.Bool, Value = 0 });
            foreach (var module in new[] { "Admission", "ReportsAnalytics", "Payouts" })
                basic.Entitlements.Add(new PlanEntitlement { Key = EntitlementKeys.ModuleKey(module), ValueType = EntitlementValueType.Bool, Value = 0 });

            var pro = NewPlan("pro", "Pro", "For academies running admissions, fees and parent updates in one place.", true, 14, 2,
                new Limits(250, 15, 50 * 1024, 12_000, 2_000, 2_000, 30), Features(recordings: true, whiteLabel: true, gateway: true, whatsapp: true),
                [(BillingInterval.Month, 2_499m), (BillingInterval.Year, 24_990m)]);
            pro.Entitlements.Add(new PlanEntitlement { Key = EntitlementKeys.ServerRecording, ValueType = EntitlementValueType.Bool, Value = 0 });

            db.Plans.AddRange(basic, pro);
            changed = true;
        }

        if (Find("starter") is { IsPublic: true } starter && StillPriced(starter, 2_999m))
        {
            starter.IsPublic = false;
            starter.UpdatedAtUtc = DateTime.UtcNow;
            changed = true;
        }

        if (Find("growth") is { } growth && StillPriced(growth, 7_999m))
        {
            foreach (var price in growth.Prices.Where(p => p.IsActive && p.Currency == "INR")) price.IsActive = false;
            db.PlanPrices.AddRange(
                new PlanPrice { PlanId = growth.Id, BillingInterval = BillingInterval.Month, Amount = 5_999m },
                new PlanPrice { PlanId = growth.Id, BillingInterval = BillingInterval.Year, Amount = 59_990m });
            growth.Description = "For multi-batch academies with larger teams, server recording and API access.";
            growth.SortOrder = 3;
            Raise(growth, EntitlementKeys.MaxStudents, 500, 750);
            Raise(growth, EntitlementKeys.MaxSessionMinutesPerMonth, 30_000, 36_000);
            Raise(growth, EntitlementKeys.RecordingRetentionDays, 30, 90);
            Flag(db, growth, EntitlementKeys.ServerRecording, 1);
            growth.UpdatedAtUtc = DateTime.UtcNow;
            changed = true;
        }

        if (Find("enterprise") is { SortOrder: 3 } enterprise) { enterprise.SortOrder = 4; changed = true; }

        if (Find(FreePlanCode) is { } free && free.Entitlements.Any(x => x.Key == EntitlementKeys.MaxStudents && x.Value == 20))
        {
            Raise(free, EntitlementKeys.MaxStudents, 20, 25);
            changed = true;
        }

        if (!changed) return;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Applied the November 2026 price list (Free, Basic, Pro, Growth).");
    }

    private sealed record Limits(long Students, long Staff, long StorageMb, long SessionMinutes,
        long Sessions, long Recordings, long RetentionDays);

    private static long DefaultUsageLimit(string planCode) => planCode switch
    {
        "trial" => 100,
        "basic" => 300,
        "starter" => 1_000,
        "pro" => 2_000,
        "growth" => 5_000,
        _ => EntitlementKeys.Unlimited,
    };

    private static void AddMissingFlag(MasterDbContext db, Plan plan, string key, long value)
    {
        if (plan.Entitlements.Any(x => x.Key == key)) return;
        db.PlanEntitlements.Add(new PlanEntitlement { PlanId = plan.Id, Key = key, ValueType = EntitlementValueType.Bool, Value = value });
    }

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
