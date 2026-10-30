using System.Text.RegularExpressions;
using iucs.meettomanage.api.Saas;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Data.Configurations;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class SaasNamingTests
{
    [Fact]
    public void UsageLimitCatalogIncludesSessionsRecordingsAndStorage()
    {
        var keys = EntitlementKeys.Catalog.Select(x => x.Key).ToHashSet();
        Assert.Contains(EntitlementKeys.MaxSessionsPerMonth, keys);
        Assert.Contains(EntitlementKeys.MaxSessionMinutesPerMonth, keys);
        Assert.Contains(EntitlementKeys.MaxRecordingsPerMonth, keys);
        Assert.Contains(EntitlementKeys.RecordingRetentionDays, keys);
        Assert.Contains(EntitlementKeys.MaxStorageMb, keys);
        Assert.Equal(keys.Count, EntitlementKeys.Catalog.Count);
    }

    [Theory]
    [InlineData("Id", "id")]
    [InlineData("TenantId", "tenant_id")]
    [InlineData("CurrentPeriodEndUtc", "current_period_end_utc")]
    [InlineData("PlanPriceId", "plan_price_id")]
    [InlineData("Gstin", "gstin")]
    public void PropertyNamesBecomeSnakeCase(string name, string expected) =>
        Assert.Equal(expected, SaasModelConfiguration.ToSnakeCase(name));

    [Fact]
    public void EverySaasTableColumnKeyAndIndexIsSnakeCase()
    {
        using var db = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql("Host=localhost;Database=none").Options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var snake = new Regex("^[a-z][a-z0-9_]*$");
        var checkedTables = new List<string>();
        foreach (var entity in model.GetEntityTypes().Where(x => x.ClrType.Namespace == typeof(Plan).Namespace))
        {
            var table = entity.GetTableName()!;
            checkedTables.Add(table);
            Assert.Matches(snake, table);
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var property in entity.GetProperties())
                Assert.Matches(snake, property.GetColumnName(store)!);
            foreach (var key in entity.GetKeys()) Assert.Matches(snake, key.GetName()!);
            foreach (var index in entity.GetIndexes()) Assert.Matches(snake, index.GetDatabaseName()!);
            foreach (var fk in entity.GetForeignKeys()) Assert.Matches(snake, fk.GetConstraintName()!);
        }
        Assert.Contains("tenant_subscription", checkedTables);
        Assert.Contains("plan_price", checkedTables);
        Assert.Equal(15, checkedTables.Count);
    }

    [Fact]
    public void TenantSubscriptionUsesTheRequestedColumnNames()
    {
        using var db = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql("Host=localhost;Database=none").Options);
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TenantSubscription))!;
        var store = StoreObjectIdentifier.Table("tenant_subscription");
        Assert.Equal("tenant_id", entity.FindProperty(nameof(TenantSubscription.TenantId))!.GetColumnName(store));
        Assert.Equal("plan_price_id", entity.FindProperty(nameof(TenantSubscription.PlanPriceId))!.GetColumnName(store));
    }

    [Fact]
    public void ExistingRegistryTablesKeepTheirOriginalColumns()
    {
        using var db = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql("Host=localhost;Database=none").Options);
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Tenant))!;
        Assert.Equal("tenants", entity.GetTableName());
        Assert.Equal("DatabaseName", entity.FindProperty(nameof(Tenant.DatabaseName))!.GetColumnName(StoreObjectIdentifier.Table("tenants")));
    }
}

public sealed class SaasRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static TenantSubscription Sub(SaasSubscriptionStatus status, Action<TenantSubscription>? tweak = null)
    {
        var sub = new TenantSubscription { Status = status };
        tweak?.Invoke(sub);
        return sub;
    }

    [Fact]
    public void UnusedCreditIsProportionalToTheRemainingPeriod()
    {
        var start = Now.AddDays(-10);
        var end = Now.AddDays(10);
        Assert.Equal(500m, SaasRules.UnusedCredit(1000m, start, end, Now));
        Assert.Equal(0m, SaasRules.UnusedCredit(1000m, start, end, end.AddDays(1)));
        Assert.Equal(1000m, SaasRules.UnusedCredit(1000m, Now, end, Now.AddDays(-5)));
    }

    [Fact]
    public void UpgradeChargeIsNeverNegative()
    {
        Assert.Equal(1500m, SaasRules.UpgradeCharge(2000m, 500m));
        Assert.Equal(0m, SaasRules.UpgradeCharge(500m, 800m));
    }

    [Fact]
    public void GstIsRoundedToTwoDecimals() => Assert.Equal(539.82m, SaasRules.TaxFor(2999m, 18m));

    [Fact]
    public void YearlyPriceIsNormalisedToMonthly()
    {
        Assert.Equal(2499.17m, SaasRules.MonthlyEquivalent(29990m, BillingInterval.Year));
        Assert.Equal(2999m, SaasRules.MonthlyEquivalent(2999m, BillingInterval.Month));
    }

    [Fact]
    public void PeriodsAdvanceByTheBillingInterval()
    {
        Assert.Equal(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc), SaasRules.AddInterval(Now, BillingInterval.Month));
        Assert.Equal(new DateTime(2027, 10, 20, 12, 0, 0, DateTimeKind.Utc), SaasRules.AddInterval(Now, BillingInterval.Year));
    }

    [Theory]
    [InlineData(-1, 5, 0, true)]
    [InlineData(100, 99, 1, true)]
    [InlineData(100, 100, 1, false)]
    [InlineData(0, 0, 0, true)]
    public void LimitsTreatMinusOneAsUnlimited(long limit, long current, long adding, bool allowed) =>
        Assert.Equal(allowed, SaasRules.WithinLimit(limit, current, adding));

    [Fact]
    public void OverridesBeatThePlanUnlessExpired()
    {
        var plan = new[]
        {
            new PlanEntitlement { Key = EntitlementKeys.MaxStudents, Value = 100 },
            new PlanEntitlement { Key = EntitlementKeys.WhiteLabel, Value = 0 },
        };
        var overrides = new[]
        {
            new TenantEntitlementOverride { Key = EntitlementKeys.MaxStudents, Value = 250 },
            new TenantEntitlementOverride { Key = EntitlementKeys.WhiteLabel, Value = 1, ExpiresAtUtc = Now.AddDays(-1) },
        };
        var resolved = SaasRules.ResolveEntitlements(plan, overrides, Now);
        Assert.Equal(250, resolved[EntitlementKeys.MaxStudents]);
        Assert.Equal(0, resolved[EntitlementKeys.WhiteLabel]);
    }

    [Fact]
    public void ExpiredTrialIsFlaggedForExpiry() => Assert.Equal(LifecycleAction.ExpireTrial,
        SaasRules.EvaluateLifecycle(Sub(SaasSubscriptionStatus.Trialing, s => s.TrialEndsAtUtc = Now.AddMinutes(-1)), Now, 7, false));

    [Fact]
    public void RunningTrialIsLeftAlone() => Assert.Equal(LifecycleAction.None,
        SaasRules.EvaluateLifecycle(Sub(SaasSubscriptionStatus.Trialing, s => s.TrialEndsAtUtc = Now.AddDays(3)), Now, 7, false));

    [Fact]
    public void RenewalInvoiceIsIssuedInsideTheLeadWindowOnlyOnce()
    {
        var sub = Sub(SaasSubscriptionStatus.Active, s => s.CurrentPeriodEndUtc = Now.AddDays(6));
        Assert.Equal(LifecycleAction.GenerateRenewalInvoice, SaasRules.EvaluateLifecycle(sub, Now, 7, false));
        Assert.Equal(LifecycleAction.None, SaasRules.EvaluateLifecycle(sub, Now, 7, true));
        sub.CurrentPeriodEndUtc = Now.AddDays(20);
        Assert.Equal(LifecycleAction.None, SaasRules.EvaluateLifecycle(sub, Now, 7, false));
    }

    [Fact]
    public void UnpaidSubscriptionBecomesPastDueAtPeriodEnd()
    {
        var sub = Sub(SaasSubscriptionStatus.Active, s => s.CurrentPeriodEndUtc = Now.AddHours(-1));
        Assert.Equal(LifecycleAction.MarkPastDue, SaasRules.EvaluateLifecycle(sub, Now, 7, true));
        Assert.Equal(LifecycleAction.GenerateRenewalInvoice, SaasRules.EvaluateLifecycle(sub, Now, 7, false));
    }

    [Fact]
    public void CancelledSubscriptionEndsAtPeriodEndAndIsNeverInvoiced()
    {
        var sub = Sub(SaasSubscriptionStatus.Active, s => { s.CancelAtPeriodEnd = true; s.CurrentPeriodEndUtc = Now.AddDays(2); });
        Assert.Equal(LifecycleAction.None, SaasRules.EvaluateLifecycle(sub, Now, 7, false));
        sub.CurrentPeriodEndUtc = Now.AddMinutes(-5);
        Assert.Equal(LifecycleAction.CancelAtPeriodEnd, SaasRules.EvaluateLifecycle(sub, Now, 7, false));
    }

    [Fact]
    public void PastDueSubscriptionIsSuspendedOnlyAfterGrace()
    {
        var sub = Sub(SaasSubscriptionStatus.PastDue, s => s.GraceEndsAtUtc = Now.AddDays(1));
        Assert.Equal(LifecycleAction.None, SaasRules.EvaluateLifecycle(sub, Now, 7, true));
        sub.GraceEndsAtUtc = Now.AddMinutes(-1);
        Assert.Equal(LifecycleAction.SuspendAfterGrace, SaasRules.EvaluateLifecycle(sub, Now, 7, true));
    }

    [Fact]
    public void ComplimentarySubscriptionWithoutAPeriodNeverChanges() => Assert.Equal(LifecycleAction.None,
        SaasRules.EvaluateLifecycle(Sub(SaasSubscriptionStatus.Active), Now.AddYears(5), 7, false));

    [Theory]
    [InlineData(6, "trial_ending_7")]
    [InlineData(2.5, "trial_ending_3")]
    [InlineData(0.5, "trial_ending_1")]
    public void TrialReminderSendsOnlyTheMostUrgentThreshold(double daysLeft, string expected)
    {
        var sub = Sub(SaasSubscriptionStatus.Trialing, s => s.TrialEndsAtUtc = Now.AddDays(daysLeft));
        var due = Assert.Single(SaasRules.TimeBasedReminders(sub, Now));
        Assert.Equal(expected, due.RuleKey);
    }

    [Fact]
    public void NoTrialReminderMoreThanSevenDaysOut() => Assert.Empty(
        SaasRules.TimeBasedReminders(Sub(SaasSubscriptionStatus.Trialing, s => s.TrialEndsAtUtc = Now.AddDays(10)), Now));

    [Fact]
    public void PastDueRemindersFollowTheDunningSchedule()
    {
        var sub = Sub(SaasSubscriptionStatus.PastDue, s =>
        {
            s.CurrentPeriodEndUtc = Now.AddDays(-3.5);
            s.GraceEndsAtUtc = Now.AddHours(20);
        });
        var rules = SaasRules.TimeBasedReminders(sub, Now).Select(x => x.RuleKey).ToList();
        Assert.Contains("past_due_day_3", rules);
        Assert.Contains("grace_ending_24h", rules);
        Assert.DoesNotContain("past_due_day_1", rules);
    }

    [Fact]
    public void ReminderCycleKeyIsStableSoItCannotRepeat()
    {
        var sub = Sub(SaasSubscriptionStatus.Trialing, s => s.TrialEndsAtUtc = Now.AddDays(2));
        Assert.Equal(SaasRules.TimeBasedReminders(sub, Now)[0], SaasRules.TimeBasedReminders(sub, Now.AddHours(5))[0]);
    }

    [Fact]
    public void BannerWarnsOnlyWhenSomethingNeedsAttention()
    {
        Assert.Null(SaasRules.Banner(Sub(SaasSubscriptionStatus.Active, s => s.CurrentPeriodEndUtc = Now.AddDays(20)), Now));
        Assert.Equal("danger", SaasRules.Banner(Sub(SaasSubscriptionStatus.PastDue), Now)!.Value.Level);
        Assert.Equal("warning", SaasRules.Banner(Sub(SaasSubscriptionStatus.Trialing, s => s.TrialEndsAtUtc = Now.AddDays(2)), Now)!.Value.Level);
        Assert.Equal("info", SaasRules.Banner(Sub(SaasSubscriptionStatus.Trialing, s => s.TrialEndsAtUtc = Now.AddDays(10)), Now)!.Value.Level);
    }
}

public sealed class SaasSeederTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestFactory _factory;

    private sealed class TestFactory(DbContextOptions<MasterDbContext> options) : IDbContextFactory<MasterDbContext>
    {
        public MasterDbContext CreateDbContext() => new(options);
    }

    public SaasSeederTests()
    {
        _connection.Open();
        _factory = new TestFactory(new DbContextOptionsBuilder<MasterDbContext>().UseSqlite(_connection).Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ExistingPlansReceiveNewUsageLimitsWithoutOverwritingOperatorValues()
    {
        var options = Options.Create(new SaasOptions());
        await SaasSeeder.SeedAsync(_factory, options, NullLogger.Instance);

        await using (var db = _factory.CreateDbContext())
        {
            var starter = await db.Plans.Include(x => x.Entitlements).SingleAsync(x => x.Code == "starter");
            Assert.Equal(1_000, starter.Entitlements.Single(x => x.Key == EntitlementKeys.MaxSessionsPerMonth).Value);
            Assert.Equal(1_000, starter.Entitlements.Single(x => x.Key == EntitlementKeys.MaxRecordingsPerMonth).Value);
            starter.Entitlements.Single(x => x.Key == EntitlementKeys.MaxSessionsPerMonth).Value = 321;
            await db.SaveChangesAsync();
        }

        await SaasSeeder.SeedAsync(_factory, options, NullLogger.Instance);

        await using var check = _factory.CreateDbContext();
        var saved = await check.PlanEntitlements.SingleAsync(x => x.Plan.Code == "starter"
            && x.Key == EntitlementKeys.MaxSessionsPerMonth);
        Assert.Equal(321, saved.Value);
    }

    public void Dispose() => _connection.Dispose();
}

public sealed class SaasEntitlementServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Tenant _tenant = new() { Code = "acme", Name = "Acme", DatabaseName = "mt_x", Status = TenantStatus.Active };
    private readonly Factory _factory;

    private sealed class Factory(DbContextOptions<MasterDbContext> options) : IDbContextFactory<MasterDbContext>
    {
        public MasterDbContext CreateDbContext() => new(options);
    }

    public SaasEntitlementServiceTests()
    {
        _connection.Open();
        _factory = new Factory(new DbContextOptionsBuilder<MasterDbContext>().UseSqlite(_connection).Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        var plan = new Plan { Code = "starter", Name = "Starter" };
        plan.Entitlements.Add(new PlanEntitlement { Key = EntitlementKeys.MaxStudents, Value = 100 });
        plan.Entitlements.Add(new PlanEntitlement { Key = EntitlementKeys.WhiteLabel, Value = 1 });
        db.Tenants.Add(_tenant);
        db.Plans.Add(plan);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = _tenant.Id, PlanId = plan.Id, Status = SaasSubscriptionStatus.Active });
        db.SaveChanges();
    }

    private EntitlementService Service() => new(_factory, new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task TenantWithoutASubscriptionIsUnrestricted()
    {
        var granted = await Service().GetAsync(Guid.NewGuid());
        Assert.False(granted.Managed);
        Assert.Equal(EntitlementKeys.Unlimited, granted.Limit(EntitlementKeys.MaxStudents));
        Assert.True(granted.Can(EntitlementKeys.ApiAccess));
    }

    [Fact]
    public async Task ManagedTenantGetsItsPlanLimitsAndOnlyGrantedFeatures()
    {
        var granted = await Service().GetAsync(_tenant.Id);
        Assert.True(granted.Managed);
        Assert.Equal(100, granted.Limit(EntitlementKeys.MaxStudents));
        Assert.Equal(EntitlementKeys.Unlimited, granted.Limit(EntitlementKeys.MaxStaff)); // plan does not define it
        Assert.True(granted.Can(EntitlementKeys.WhiteLabel));
        Assert.False(granted.Can(EntitlementKeys.ApiAccess)); // not granted
    }

    [Fact]
    public async Task OverrideAppliesAfterInvalidation()
    {
        var service = Service();
        Assert.Equal(100, (await service.GetAsync(_tenant.Id)).Limit(EntitlementKeys.MaxStudents));
        using (var db = _factory.CreateDbContext())
        {
            db.TenantEntitlementOverrides.Add(new TenantEntitlementOverride { TenantId = _tenant.Id, Key = EntitlementKeys.MaxStudents, Value = 300 });
            db.SaveChanges();
        }
        Assert.Equal(100, (await service.GetAsync(_tenant.Id)).Limit(EntitlementKeys.MaxStudents)); // still cached
        service.Invalidate(_tenant.Id);
        Assert.Equal(300, (await service.GetAsync(_tenant.Id)).Limit(EntitlementKeys.MaxStudents));
    }

    [Fact]
    public async Task OneTenantPerSubscriptionIsEnforcedByTheDatabase()
    {
        using var db = _factory.CreateDbContext();
        var plan = await db.Plans.FirstAsync();
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = _tenant.Id, PlanId = plan.Id, Status = SaasSubscriptionStatus.Active });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    public void Dispose() => _connection.Dispose();
}
