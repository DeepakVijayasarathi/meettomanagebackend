using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Saas;

/// <summary>
/// Shared helper for the SaaS workers. The workers run in every API instance, so each cycle first takes a
/// transaction-scoped PostgreSQL advisory lock on the master database; an instance that does not get it skips the
/// cycle. That keeps invoices, status changes and reminders single-run without any extra infrastructure.
/// </summary>
internal static class SaasWorkerLock
{
    public static async Task<bool> RunExclusiveAsync(IDbContextFactory<MasterDbContext> factory, string name,
        Func<CancellationToken, Task> work, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtext({name})) AS \"Value\"").SingleAsync(ct);
        if (!acquired) return false;
        await work(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}

/// <summary>Runs the billing cycle (trial expiry, renewal invoices, past-due, suspension) and the time-based reminders.</summary>
public sealed class SaasLifecycleBackgroundService(IDbContextFactory<MasterDbContext> factory, SaasSubscriptionService subscriptions,
    SaasReminderService reminders, ILogger<SaasLifecycleBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let start-up migrations and seeding finish first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SaaS billing cycle failed; retrying next interval.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct) => await SaasWorkerLock.RunExclusiveAsync(factory, "saas-lifecycle", async token =>
    {
        var now = DateTime.UtcNow;
        var handled = await subscriptions.RunLifecycleAsync(now, token);
        await reminders.RunTimeBasedAsync(now, token);
        if (handled > 0) logger.LogInformation("SaaS billing cycle handled {Count} subscription(s).", handled);
    }, ct);
}

/// <summary>Collects each active tenant's daily usage snapshot and sends the 80% / 100% limit reminders.</summary>
public sealed class SaasUsageBackgroundService(IDbContextFactory<MasterDbContext> factory, SaasUsageService usage,
    IEntitlementService entitlements, SaasReminderService reminders, ILogger<SaasUsageBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SaaS usage snapshot failed; retrying next interval.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct) => await SaasWorkerLock.RunExclusiveAsync(factory, "saas-usage", async token =>
    {
        var month = DateTime.UtcNow.ToString("yyyyMM");
        foreach (var (tenantId, figures) in await usage.SnapshotAllAsync(token))
        {
            var granted = await entitlements.GetAsync(tenantId, token);
            if (!granted.Managed) continue;
            foreach (var key in SaasUsageService.MeteredKeys)
            {
                var limit = granted.Limit(key);
                var current = figures.For(key) ?? 0;
                if (limit <= 0) continue;
                var percent = (int)(current * 100 / limit);
                var rule = percent >= 100 ? "usage_limit_100" : percent >= 80 ? "usage_limit_80" : null;
                if (rule is null) continue;
                var definition = EntitlementKeys.Find(key)!;
                await reminders.NotifyAsync(tenantId, rule, $"{key}:{month}", extra: new Dictionary<string, string>
                {
                    ["usage_label"] = definition.Label.ToLowerInvariant(),
                    ["usage_percent"] = Math.Min(percent, 999).ToString(),
                    ["usage_limit"] = $"{limit} {definition.Unit}",
                    ["plan_name"] = granted.PlanName ?? "",
                }, ct: token);
            }
        }
    }, ct);
}
