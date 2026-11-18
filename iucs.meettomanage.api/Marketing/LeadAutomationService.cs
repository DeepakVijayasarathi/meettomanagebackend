using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Settings;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Marketing;

/// <summary>
/// The scheduled half of the lead lifecycle (the instant half, acknowledgement and owner assignment, runs when the
/// lead is created): demo reminders, matching leads to the academy accounts they open, and flagging leads that keep
/// returning to pricing. Also builds the console's funnel report. Runs in every workspace; only the marketing
/// workspace has leads, so elsewhere each pass finds nothing to do.
/// </summary>
public sealed class LeadAutomationService(MeetToManageDbContext db, IEmailSender email, IConfiguration configuration,
    IServiceProvider services, ILogger<LeadAutomationService> logger)
{
    private string SiteUrl => (configuration["Marketing:SiteUrl"] ?? "https://meettomanage.cloud").TrimEnd('/');
    private string TimeZoneId => configuration["Marketing:TimeZone"] ?? "Asia/Kolkata";

    /// <summary>The master database exists only in multi-tenant mode; without it leads can't be matched to accounts.</summary>
    private IDbContextFactory<MasterDbContext>? Master => services.GetService<IDbContextFactory<MasterDbContext>>();

    public async Task<Dictionary<string, string>> SettingsAsync(CancellationToken ct)
    {
        var rows = await db.AppSettings.AsNoTracking().Where(x => x.Key.StartsWith(LeadLifecycle.SettingPrefix)).ToDictionaryAsync(x => x.Key, x => x.Value ?? "", ct);
        var result = LeadLifecycle.Toggles.ToDictionary(k => k, k => rows.TryGetValue(k, out var v) && v.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) ? "false" : "true");
        result[LeadLifecycle.BookingUrl] = rows.GetValueOrDefault(LeadLifecycle.BookingUrl) ?? "";
        return result;
    }

    private static bool On(Dictionary<string, string> settings, string key) => settings.GetValueOrDefault(key) != "false";

    public async Task SaveSettingsAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        foreach (var (key, raw) in values)
        {
            string value;
            if (LeadLifecycle.Toggles.Contains(key)) value = raw.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
            else if (key == LeadLifecycle.BookingUrl)
            {
                value = raw.Trim();
                if (value.Length > 0 && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || value.Length > 500))
                    throw new application.Common.Exceptions.DomainValidationException("The booking link must be a full https:// address.");
            }
            else continue;
            var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (row is null) db.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = key, Value = value, Category = SettingCategory.General, IsPublic = false });
            else row.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>One pass of every scheduled automation. Each part is independent, so one failing doesn't stop the rest.</summary>
    public async Task RunAsync(DateTime now, CancellationToken ct)
    {
        if (!await db.PlatformDemoRequests.AnyAsync(ct)) return;
        var settings = await SettingsAsync(ct);
        if (On(settings, LeadLifecycle.DemoReminders)) await Guarded("demo reminders", () => SendDemoRemindersAsync(now, ct));
        if (On(settings, LeadLifecycle.TrialSync)) await Guarded("trial sync", () => SyncAccountsAsync(now, ct));
        if (On(settings, LeadLifecycle.HighIntentAlerts)) await Guarded("high-intent alerts", () => FlagHighIntentAsync(now, ct));
        await Guarded("follow-up plans", () => StopFinishedPlansAsync(now, ct));
    }

    /// <summary>
    /// Closes the open steps of a follow-up plan once its lead reaches one of the plan's stop stages (a demo
    /// booked, a sale, a loss), so nobody keeps chasing a lead that has already moved on.
    /// </summary>
    internal async Task<int> StopFinishedPlansAsync(DateTime now, CancellationToken ct)
    {
        var open = await db.CrmTasks.Where(x => x.PlanKey != null && x.CompletedAtUtc == null && x.LeadId != null)
            .Join(db.PlatformDemoRequests, t => t.LeadId, l => l.Id, (t, l) => new { Task = t, l.Stage })
            .ToListAsync(ct);
        var closed = 0;
        foreach (var row in open)
        {
            if (CrmRules.Plan(row.Task.PlanKey) is not { } plan || !plan.StopAt.Contains(row.Stage)) continue;
            row.Task.CompletedAtUtc = now;
            row.Task.Outcome = $"Stopped automatically: lead is now {LeadLifecycle.Label(row.Stage).ToLowerInvariant()}";
            closed++;
        }
        if (closed > 0) await db.SaveChangesAsync(ct);
        return closed;
    }

    private async Task Guarded(string name, Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Lead automation step '{Step}' failed; it will retry on the next pass.", name);
            db.ChangeTracker.Clear();
        }
    }

    private void Log(Guid leadId, string type, string detail) =>
        db.MarketingLeadActivities.Add(new MarketingLeadActivity { Id = Guid.NewGuid(), PlatformDemoRequestId = leadId, Type = type, Detail = detail });

    // ------------------------------------------------------------------ demo reminders

    private async Task SendDemoRemindersAsync(DateTime now, CancellationToken ct)
    {
        var soon = now.AddHours(25);
        var leads = await db.PlatformDemoRequests.Where(x => x.ArchivedAtUtc == null && x.Stage == LeadStage.DemoBooked
            && x.DemoAtUtc > now && x.DemoAtUtc < soon && x.WorkEmail != "").ToListAsync(ct);
        foreach (var lead in leads)
        {
            var due = LeadLifecycle.ReminderDue(lead, now);
            if (due == LeadLifecycle.DemoReminder.None || !PlatformDemoRequestService.LooksLikeEmail(lead.WorkEmail)) continue;
            var when = LeadEmails.FormatDemoTime(lead.DemoAtUtc!.Value, TimeZoneId);
            var mail = due == LeadLifecycle.DemoReminder.HourBefore ? LeadEmails.DemoReminderHour(lead, when) : LeadEmails.DemoReminderDay(lead, when);
            // Mark first, then send: a crash between the two skips one reminder rather than sending it twice.
            if (due == LeadLifecycle.DemoReminder.HourBefore) lead.DemoReminderHourSentAtUtc = now; else lead.DemoReminderDaySentAtUtc = now;
            await db.SaveChangesAsync(ct);
            try
            {
                await email.SendAsync(lead.WorkEmail, mail.Subject, mail.Html, ct);
                Log(lead.Id, LeadLifecycle.AutomationActivity, due == LeadLifecycle.DemoReminder.HourBefore ? "Demo reminder sent (1 hour before)." : "Demo reminder sent (day before).");
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Demo reminder for lead {Id} failed.", lead.Id);
            }
        }
    }

    // ------------------------------------------------------------------ leads -> academy accounts

    private async Task SyncAccountsAsync(DateTime now, CancellationToken ct)
    {
        if (Master is not { } factory) return;
        // This worker runs in every tenant. Only the platform owner's leads may be matched against other tenants'
        // accounts; anywhere else it would copy another academy's plan and usage into this tenant's database.
        if (services.GetService<TenantContext>()?.TenantId is not { } self) return;
        await using (var check = await factory.CreateDbContextAsync(ct))
            if (await TenantResolver.PlatformOwnerTenantIdAsync(check, configuration, ct) != self) return;
        var since = now.AddDays(-365);
        var leads = await db.PlatformDemoRequests.Where(x => x.ArchivedAtUtc == null && x.Stage != LeadStage.Customer
            && (x.TenantId != null || (x.WorkEmail != "" && x.CreatedAtUtc > since))).OrderByDescending(x => x.CreatedAtUtc).Take(1000).ToListAsync(ct);
        if (leads.Count == 0) return;

        await using var master = await factory.CreateDbContextAsync(ct);
        var emails = leads.Where(x => x.TenantId is null).Select(x => x.WorkEmail.ToLowerInvariant()).Distinct().ToList();
        var byEmail = (await master.TenantBillingProfiles.AsNoTracking().Where(x => x.BillingEmail != null && emails.Contains(x.BillingEmail.ToLower()))
            .Select(x => new { x.TenantId, Email = x.BillingEmail!.ToLower() }).ToListAsync(ct))
            .GroupBy(x => x.Email).ToDictionary(g => g.Key, g => g.First().TenantId);
        foreach (var lead in leads.Where(x => x.TenantId is null))
            if (byEmail.TryGetValue(lead.WorkEmail.ToLowerInvariant(), out var tenantId)) lead.TenantId = tenantId;

        var tenantIds = leads.Where(x => x.TenantId is not null).Select(x => x.TenantId!.Value).Distinct().ToList();
        if (tenantIds.Count == 0) return;
        var tenants = await master.Tenants.AsNoTracking().Where(x => tenantIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var subs = await master.TenantSubscriptions.AsNoTracking().Include(x => x.PlanPrice).Where(x => tenantIds.Contains(x.TenantId)).ToDictionaryAsync(x => x.TenantId, ct);
        var recent = DateOnly.FromDateTime(now.AddDays(-3));
        var usage = (await master.TenantUsageSnapshots.AsNoTracking().Where(x => tenantIds.Contains(x.TenantId) && x.SnapshotDate >= recent).ToListAsync(ct))
            .GroupBy(x => x.TenantId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.SnapshotDate).First());

        foreach (var lead in leads.Where(x => x.TenantId is not null))
        {
            var tenantId = lead.TenantId!.Value;
            if (lead.TenantCode is null && tenants.TryGetValue(tenantId, out var code))
            {
                lead.TenantCode = code;
                Log(lead.Id, LeadLifecycle.AutomationActivity, $"Matched to academy account \"{code}\" by e-mail.");
            }
            if (!subs.TryGetValue(tenantId, out var sub)) continue;
            var next = LeadLifecycle.StageFromAccount(Account(sub, usage.GetValueOrDefault(tenantId)));
            if (next is not { } stage || !LeadLifecycle.ShouldMove(lead.Stage, stage)) continue;
            LeadLifecycle.Apply(lead, stage, now);
            if (stage == LeadStage.Lost) lead.LostReason = "Trial ended without a paid plan";
            Log(lead.Id, LeadLifecycle.AutomationActivity, stage switch
            {
                LeadStage.Trial => "Started a trial (academy account found).",
                LeadStage.ActiveTrial => "Trial is active: students added and a live class held.",
                LeadStage.Customer => "Became a paying customer.",
                _ => $"Moved to {LeadLifecycle.Label(stage)} from the academy account.",
            });
        }
        await db.SaveChangesAsync(ct);
    }

    internal static AccountSnapshot Account(TenantSubscription sub, TenantUsageSnapshot? usage)
    {
        var paid = (sub.PlanPrice?.Amount ?? 0) > 0;
        return new AccountSnapshot(
            IsTrialOrFree: sub.Status == SaasSubscriptionStatus.Trialing || (sub.Status == SaasSubscriptionStatus.Active && !paid),
            IsPaidActive: paid && sub.Status is SaasSubscriptionStatus.Active or SaasSubscriptionStatus.PastDue,
            Ended: sub.Status is SaasSubscriptionStatus.Expired or SaasSubscriptionStatus.Cancelled,
            ActiveStudents: usage?.ActiveStudents ?? 0,
            SessionsThisMonth: usage?.SessionsMonth ?? 0);
    }

    // ------------------------------------------------------------------ high intent

    private async Task FlagHighIntentAsync(DateTime now, CancellationToken ct)
    {
        var watch = (await db.PlatformDemoRequests.Where(x => x.ArchivedAtUtc == null && x.HighIntentAtUtc == null && x.VisitorId != null
                && (x.Stage == LeadStage.DemoDone || x.Stage == LeadStage.Trial || x.Stage == LeadStage.ActiveTrial)).ToListAsync(ct))
            .Where(LeadLifecycle.WatchForIntent).ToList();
        if (watch.Count == 0) return;
        var windowStart = now.AddDays(-LeadLifecycle.HighIntentWindowDays);
        var visitors = watch.Select(x => x.VisitorId!).Distinct().ToList();
        var views = await db.MarketingEvents.AsNoTracking()
            .Where(x => visitors.Contains(x.VisitorId) && x.Type == MarketingEvent.PageView && x.Path == "/pricing" && x.CreatedAtUtc > windowStart)
            .Select(x => new { x.VisitorId, x.CreatedAtUtc }).ToListAsync(ct);
        var alertTo = (configuration["Marketing:LeadAlertEmail"] ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var lead in watch)
        {
            var count = views.Count(v => v.VisitorId == lead.VisitorId && v.CreatedAtUtc > lead.CreatedAtUtc);
            if (count < LeadLifecycle.HighIntentPricingViews) continue;
            lead.HighIntentAtUtc = now;
            Log(lead.Id, LeadLifecycle.AutomationActivity, $"High intent: viewed pricing {count} times in {LeadLifecycle.HighIntentWindowDays} days. Team alerted.");
            await db.SaveChangesAsync(ct);
            var owner = lead.AssignedToUserId is { } ownerId ? await db.Users.AsNoTracking().Where(x => x.Id == ownerId).Select(x => x.Email).FirstOrDefaultAsync(ct) : null;
            var mail = LeadEmails.HighIntentAlert(lead, $"{SiteUrl}/marketing/demo-requests", count);
            foreach (var to in alertTo.Append(owner ?? "").Where(PlatformDemoRequestService.LooksLikeEmail).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { await email.SendAsync(to, mail.Subject, mail.Html, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "High-intent alert for lead {Id} to {To} failed.", lead.Id, to); }
            }
        }
    }

    // ------------------------------------------------------------------ console report

    /// <summary>Leads a sales rep may see (their own and unassigned); everything when <paramref name="visibleTo"/> is null.</summary>
    private IQueryable<PlatformDemoRequest> Visible(Guid? visibleTo) =>
        visibleTo is { } me
            ? db.PlatformDemoRequests.AsNoTracking().Where(x => x.AssignedToUserId == null || x.AssignedToUserId == me)
            : db.PlatformDemoRequests.AsNoTracking();

    public async Task<LeadAutomationOverview> OverviewAsync(int days, CancellationToken ct, Guid? visibleTo = null)
    {
        var now = DateTime.UtcNow;
        days = Math.Clamp(days, 7, 365);
        var since = now.AddDays(-days);
        var leads = await Visible(visibleTo).Where(x => x.CreatedAtUtc >= since)
            .Select(x => new { x.Id, x.Stage, x.Source, x.CreatedAtUtc, x.FirstContactedAtUtc, x.AcknowledgedAtUtc, x.WorkEmail, x.LostReason }).ToListAsync(ct);

        static int Rank(LeadStage s) => s switch { LeadStage.New => 0, LeadStage.Contacted => 1, LeadStage.DemoBooked => 2, LeadStage.DemoDone => 3, LeadStage.Trial => 4, LeadStage.ActiveTrial => 5, LeadStage.Customer => 6, _ => -1 };
        var funnel = new[] { LeadStage.New, LeadStage.Contacted, LeadStage.DemoBooked, LeadStage.DemoDone, LeadStage.Trial, LeadStage.ActiveTrial, LeadStage.Customer }
            .Select(stage => new FunnelStep(stage, LeadLifecycle.Label(stage), leads.Count(l => l.Stage == stage), leads.Count(l => Rank(l.Stage) >= Rank(stage))))
            .ToList();
        var responses = leads.Where(l => l.FirstContactedAtUtc is not null).Select(l => (l.FirstContactedAtUtc!.Value - l.CreatedAtUtc).TotalHours).Where(h => h >= 0).OrderBy(h => h).ToList();
        double? median = responses.Count == 0 ? null : responses.Count % 2 == 1 ? responses[responses.Count / 2] : (responses[responses.Count / 2 - 1] + responses[responses.Count / 2]) / 2;
        var withEmail = leads.Count(l => PlatformDemoRequestService.LooksLikeEmail(l.WorkEmail));

        var sources = leads.GroupBy(l => string.IsNullOrWhiteSpace(l.Source) ? "Direct" : l.Source!)
            .Select(g => new SourceRow(g.Key, g.Count(), g.Count(l => Rank(l.Stage) >= Rank(LeadStage.DemoBooked)), g.Count(l => Rank(l.Stage) >= Rank(LeadStage.Trial)), g.Count(l => l.Stage == LeadStage.Customer)))
            .OrderByDescending(x => x.Leads).Take(12).ToList();
        var lostReasons = leads.Where(l => l.Stage == LeadStage.Lost).GroupBy(l => l.LostReason ?? "No reason given")
            .Select(g => new CountRow(g.Key, g.Count())).OrderByDescending(x => x.Count).Take(8).ToList();

        var weekAhead = now.AddDays(7);
        var upcoming = await Visible(visibleTo).Where(x => x.ArchivedAtUtc == null && x.Stage == LeadStage.DemoBooked && x.DemoAtUtc >= now.AddHours(-2) && x.DemoAtUtc <= weekAhead)
            .OrderBy(x => x.DemoAtUtc).Take(20).Select(x => new LeadRow(x.Id, x.FullName, x.AcademyName, x.AssignedTo, x.DemoAtUtc, x.Stage)).ToListAsync(ct);
        var hot = await Visible(visibleTo).Where(x => x.ArchivedAtUtc == null && x.HighIntentAtUtc >= now.AddDays(-14) && x.Stage != LeadStage.Customer && x.Stage != LeadStage.Lost)
            .OrderByDescending(x => x.HighIntentAtUtc).Take(20).Select(x => new LeadRow(x.Id, x.FullName, x.AcademyName, x.AssignedTo, x.HighIntentAtUtc, x.Stage)).ToListAsync(ct);
        var stale = await Visible(visibleTo).CountAsync(x => x.ArchivedAtUtc == null && x.Stage == LeadStage.New && x.CreatedAtUtc < now.AddHours(-24), ct);
        var activity = await db.MarketingLeadActivities.AsNoTracking().Where(x => x.Type == LeadLifecycle.AutomationActivity)
            .Join(Visible(visibleTo), a => a.PlatformDemoRequestId, l => l.Id, (a, l) => new { l.Id, l.FullName, a.Detail, a.CreatedAtUtc })
            .OrderByDescending(x => x.CreatedAtUtc).Take(25)
            .Select(x => new ActivityRow(x.Id, x.FullName, x.Detail, x.CreatedAtUtc))
            .ToListAsync(ct);

        return new LeadAutomationOverview(days, leads.Count, funnel, leads.Count(l => l.Stage == LeadStage.Lost), median,
            responses.Count == 0 ? null : Math.Round(100.0 * responses.Count(h => h <= 24) / responses.Count, 1),
            withEmail == 0 ? null : Math.Round(100.0 * leads.Count(l => l.AcknowledgedAtUtc is not null) / withEmail, 1),
            stale, sources, lostReasons, upcoming, hot, activity, await SettingsAsync(ct), Master is not null);
    }
}

public sealed record FunnelStep(LeadStage Stage, string Label, int Current, int Reached);
public sealed record SourceRow(string Source, int Leads, int Demos, int Trials, int Customers);
public sealed record CountRow(string Label, int Count);
public sealed record LeadRow(Guid Id, string FullName, string AcademyName, string? Owner, DateTime? AtUtc, LeadStage Stage);
public sealed record ActivityRow(Guid LeadId, string FullName, string Detail, DateTime AtUtc);
public sealed record LeadAutomationOverview(int Days, int Leads, List<FunnelStep> Funnel, int Lost, double? MedianFirstResponseHours,
    double? ContactedWithin24hPercent, double? AcknowledgedPercent, int UncontactedOver24h, List<SourceRow> Sources, List<CountRow> LostReasons,
    List<LeadRow> UpcomingDemos, List<LeadRow> HighIntent, List<ActivityRow> RecentAutomation, Dictionary<string, string> Settings, bool AccountSyncAvailable);

/// <summary>Runs the scheduled lead automations every 10 minutes.</summary>
public sealed class LeadAutomationBackgroundService(IServiceScopeFactory scopes, ILogger<LeadAutomationBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<LeadAutomationService>().RunAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lead automation pass failed; retrying next cycle.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
