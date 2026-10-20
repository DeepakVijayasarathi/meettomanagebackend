using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace iucs.meettomanage.api.Saas;

/// <summary>
/// Sends the billing and lifecycle emails. Every send is first recorded in subscription_reminder_log under a
/// unique (subscription, rule, cycle, channel) key, which is what makes a reminder at-most-once across API
/// instances and worker restarts. This service never throws into billing code.
/// </summary>
public sealed partial class SaasReminderService(IDbContextFactory<MasterDbContext> factory, PlatformEmailSender email,
    TenantScopeFactory scopes, IOptions<SaasOptions> options, IConfiguration configuration, ILogger<SaasReminderService> logger)
{
    private const int MaxAttempts = 3;

    /// <summary>Sends one reminder unless it was already delivered for this cycle. Returns true when an email went out.</summary>
    public async Task<bool> NotifyAsync(Guid tenantId, string ruleKey, string cycleKey, Guid? invoiceId = null,
        IReadOnlyDictionary<string, string>? extra = null, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct);
            var sub = await db.TenantSubscriptions.AsNoTracking().Include(x => x.Plan).Include(x => x.PlanPrice)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (tenant is null || sub is null) return false;

            var template = await db.PlatformEmailTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Key == ruleKey, ct);
            var fallback = DefaultEmailTemplates.All.FirstOrDefault(x => x.Key == ruleKey);
            if (template is { IsActive: false } || (template is null && fallback is null)) return false;
            var subject = template?.Subject ?? fallback!.Subject;
            var body = template?.Body ?? fallback!.Body;

            var log = await ClaimAsync(db, tenantId, sub.Id, ruleKey, cycleKey, ct);
            if (log is null) return false;

            var profile = await db.TenantBillingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            var recipient = await ResolveRecipientAsync(tenant, profile, ct);
            log.Recipient = recipient;
            if (string.IsNullOrWhiteSpace(recipient))
            {
                await FinishAsync(db, log, ReminderLogStatus.Skipped, "No billing e-mail address is known for this tenant.", ct);
                return false;
            }

            PlatformInvoice? invoice = null;
            if (invoiceId is { } id) invoice = await db.PlatformInvoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            invoice ??= await db.PlatformInvoices.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status == PlatformInvoiceStatus.Open)
                .OrderByDescending(x => x.IssuedAtUtc).FirstOrDefaultAsync(ct);

            var values = BuildValues(tenant, sub, invoice, extra);
            try
            {
                var result = await email.SendAsync(recipient, Render(subject, values), Render(body, values), ct);
                await FinishAsync(db, log, result.Delivered ? ReminderLogStatus.Sent : ReminderLogStatus.Skipped, result.SkippedReason, ct);
                return result.Delivered;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Billing e-mail {Rule} to tenant {TenantId} failed (attempt {Attempt}).", ruleKey, tenantId, log.Attempts);
                await FinishAsync(db, log, ReminderLogStatus.Failed, Truncate(ex.Message, 480), ct);
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reminder {Rule} for tenant {TenantId} could not be processed.", ruleKey, tenantId);
            return false;
        }
    }

    /// <summary>Evaluates the time-based rules for every live subscription.</summary>
    public async Task RunTimeBasedAsync(DateTime now, CancellationToken ct)
    {
        List<TenantSubscription> subscriptions;
        await using (var db = await factory.CreateDbContextAsync(ct))
            subscriptions = await db.TenantSubscriptions.AsNoTracking().Where(x => x.Status == SaasSubscriptionStatus.Trialing
                || x.Status == SaasSubscriptionStatus.PastDue).ToListAsync(ct);
        foreach (var sub in subscriptions)
            foreach (var due in SaasRules.TimeBasedReminders(sub, now))
                await NotifyAsync(sub.TenantId, due.RuleKey, due.CycleKey, ct: ct);
    }

    /// <summary>Inserts the log row, or reclaims a failed one that still has attempts left. Null means "already handled".</summary>
    private static async Task<SubscriptionReminderLog?> ClaimAsync(MasterDbContext db, Guid tenantId, Guid subscriptionId,
        string ruleKey, string cycleKey, CancellationToken ct)
    {
        var existing = await db.SubscriptionReminderLogs.SingleOrDefaultAsync(x => x.SubscriptionId == subscriptionId
            && x.RuleKey == ruleKey && x.CycleKey == cycleKey && x.Channel == "email", ct);
        if (existing is not null)
        {
            if (existing.Status != ReminderLogStatus.Failed || existing.Attempts >= MaxAttempts) return null;
            existing.Attempts++;
            existing.Status = ReminderLogStatus.Pending;
            await db.SaveChangesAsync(ct);
            return existing;
        }
        var log = new SubscriptionReminderLog
        {
            TenantId = tenantId, SubscriptionId = subscriptionId, RuleKey = ruleKey, CycleKey = cycleKey,
            Channel = "email", Status = ReminderLogStatus.Pending, Attempts = 1,
        };
        db.SubscriptionReminderLogs.Add(log);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return null; // another instance claimed it first
        }
        return log;
    }

    private static async Task FinishAsync(MasterDbContext db, SubscriptionReminderLog log, ReminderLogStatus status, string? error, CancellationToken ct)
    {
        log.Status = status;
        log.Error = error;
        if (status == ReminderLogStatus.Sent) log.SentAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Billing profile e-mail first; otherwise the tenant's own administrator account.</summary>
    private async Task<string?> ResolveRecipientAsync(Tenant tenant, TenantBillingProfile? profile, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(profile?.BillingEmail)) return profile.BillingEmail;
        try
        {
            using var ambient = TenantExecution.Enter(tenant.Id, tenant.Code);
            using var scope = scopes.CreateScope(tenant);
            var business = scope.ServiceProvider.GetRequiredService<MeetToManageDbContext>();
            return await business.Users.AsNoTracking().Where(x => x.Role == UserRole.Admin && x.Status == UserStatus.Active)
                .OrderBy(x => x.CreatedAtUtc).Select(x => x.Email).FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not look up the administrator e-mail for tenant {TenantId}.", tenant.Id);
            return null;
        }
    }

    private Dictionary<string, string> BuildValues(Tenant tenant, TenantSubscription sub, PlatformInvoice? invoice,
        IReadOnlyDictionary<string, string>? extra)
    {
        var opt = options.Value;
        var culture = CultureInfo.GetCultureInfo("en-IN");
        string Date(DateTime? value) => value?.ToString("dd MMM yyyy", culture) ?? "-";
        var frontend = (configuration["Frontend:BaseUrl"] ?? "").TrimEnd('/');
        var plan = sub.Plan?.Name ?? "";
        if (invoice?.TargetPlanPriceId is not null && sub.PlanPriceId != invoice.TargetPlanPriceId) plan = sub.Plan?.Name ?? plan;
        var values = new Dictionary<string, string>
        {
            ["tenant_name"] = tenant.Name,
            ["tenant_code"] = tenant.Code,
            ["plan_name"] = plan,
            ["platform_name"] = opt.PlatformName,
            ["support_email"] = opt.SupportEmail,
            ["trial_end_date"] = Date(sub.TrialEndsAtUtc),
            ["period_end_date"] = Date(sub.CurrentPeriodEndUtc),
            ["grace_end_date"] = Date(sub.GraceEndsAtUtc),
            ["portal_url"] = frontend.Length == 0 ? "" : $"{frontend}/t/{tenant.Code}/admin/subscription",
            ["invoice_number"] = invoice?.InvoiceNumber ?? "",
            ["amount"] = invoice is null ? "" : $"{invoice.Currency} {invoice.Total.ToString("N2", culture)}",
            ["due_date"] = Date(invoice?.DueAtUtc),
            ["payment_url"] = invoice?.PaymentUrl ?? "",
        };
        if (extra is not null) foreach (var (key, value) in extra) values[key] = value;
        return values;
    }

    /// <summary>Replaces {{placeholders}}; values are HTML-encoded, except the prebuilt pay block.</summary>
    private string Render(string template, Dictionary<string, string> values)
    {
        var pay = values.GetValueOrDefault("payment_url") is { Length: > 0 } url
            ? $"<p><a href=\"{WebUtility.HtmlEncode(url)}\" style=\"background:#1e3a5f;color:#fff;padding:10px 18px;border-radius:8px;text-decoration:none\">Pay invoice {WebUtility.HtmlEncode(values.GetValueOrDefault("invoice_number") ?? "")} ({WebUtility.HtmlEncode(values.GetValueOrDefault("amount") ?? "")})</a></p>"
            : "<p>To pay, sign in to your billing page or contact " + WebUtility.HtmlEncode(options.Value.SupportEmail) + ".</p>";
        return Placeholder().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (key == "pay_block") return pay;
            return values.TryGetValue(key, out var value) ? WebUtility.HtmlEncode(value) : "";
        });
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"\{\{\s*([a-z0-9_]+)\s*\}\}")]
    private static partial Regex Placeholder();
}
