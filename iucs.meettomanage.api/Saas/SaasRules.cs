using iucs.meettomanage.domain.Entities.Saas;

namespace iucs.meettomanage.api.Saas;

public enum LifecycleAction { None, ExpireTrial, GenerateRenewalInvoice, MarkPastDue, CancelAtPeriodEnd, SuspendAfterGrace }

public sealed record ReminderDue(string RuleKey, string CycleKey);

/// <summary>
/// The commercial rules of the subscription lifecycle as pure functions (no database, no clock),
/// so every transition and amount can be unit tested.
/// </summary>
public static class SaasRules
{
    public static DateTime AddInterval(DateTime from, BillingInterval interval) =>
        interval == BillingInterval.Year ? from.AddYears(1) : from.AddMonths(1);

    /// <summary>Price normalised to one month, to compare monthly and yearly prices and to compute MRR.</summary>
    public static decimal MonthlyEquivalent(decimal amount, BillingInterval interval) =>
        interval == BillingInterval.Year ? Math.Round(amount / 12m, 2) : amount;

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal TaxFor(decimal subtotal, decimal ratePercent) => Round(subtotal * ratePercent / 100m);

    /// <summary>Value of the unused part of the current period, credited when moving to a higher plan.</summary>
    public static decimal UnusedCredit(decimal paidAmount, DateTime periodStart, DateTime periodEnd, DateTime now)
    {
        var total = (periodEnd - periodStart).TotalSeconds;
        if (total <= 0 || now >= periodEnd) return 0m;
        var remaining = (periodEnd - (now < periodStart ? periodStart : now)).TotalSeconds;
        return Round(paidAmount * (decimal)(remaining / total));
    }

    /// <summary>What an upgrade costs: the new price less the unused credit, never negative.</summary>
    public static decimal UpgradeCharge(decimal newAmount, decimal credit) => Math.Max(0m, Round(newAmount - credit));

    /// <summary>Applies a limit: -1 is unlimited, otherwise current + adding must not exceed it.</summary>
    public static bool WithinLimit(long limit, long current, long adding = 0) =>
        limit == EntitlementKeys.Unlimited || current + adding <= limit;

    /// <summary>Plan values with the tenant's non-expired overrides on top.</summary>
    public static Dictionary<string, long> ResolveEntitlements(
        IEnumerable<PlanEntitlement> plan, IEnumerable<TenantEntitlementOverride> overrides, DateTime now)
    {
        var values = plan.ToDictionary(x => x.Key, x => x.Value);
        foreach (var item in overrides.Where(x => x.ExpiresAtUtc is null || x.ExpiresAtUtc > now))
            values[item.Key] = item.Value;
        return values;
    }

    /// <summary>The single next step the billing cycle should take for a subscription, if any.</summary>
    public static LifecycleAction EvaluateLifecycle(TenantSubscription sub, DateTime now, int invoiceLeadDays, bool hasOpenRenewalInvoice)
    {
        switch (sub.Status)
        {
            case SaasSubscriptionStatus.Trialing when sub.TrialEndsAtUtc is { } trialEnd && trialEnd <= now:
                return LifecycleAction.ExpireTrial;

            case SaasSubscriptionStatus.Active when sub.CurrentPeriodEndUtc is { } end:
                if (sub.CancelAtPeriodEnd) return now >= end ? LifecycleAction.CancelAtPeriodEnd : LifecycleAction.None;
                if (now >= end) return hasOpenRenewalInvoice ? LifecycleAction.MarkPastDue : LifecycleAction.GenerateRenewalInvoice;
                if (!hasOpenRenewalInvoice && now >= end.AddDays(-invoiceLeadDays)) return LifecycleAction.GenerateRenewalInvoice;
                return LifecycleAction.None;

            case SaasSubscriptionStatus.PastDue when sub.GraceEndsAtUtc is { } graceEnd && now >= graceEnd:
                return LifecycleAction.SuspendAfterGrace;
        }
        return LifecycleAction.None;
    }

    private static readonly int[] TrialThresholdDays = [7, 3, 1];
    private static readonly int[] PastDueDays = [1, 3, 5];
    private static readonly int[] GraceThresholdHours = [48, 24];

    /// <summary>
    /// Reminders that depend only on the passage of time. Only the most urgent threshold crossed is
    /// returned, so a worker that was offline does not flood the tenant with every missed step; the
    /// reminder log's unique key keeps each one at-most-once.
    /// </summary>
    public static IReadOnlyList<ReminderDue> TimeBasedReminders(TenantSubscription sub, DateTime now)
    {
        var due = new List<ReminderDue>();
        switch (sub.Status)
        {
            case SaasSubscriptionStatus.Trialing when sub.TrialEndsAtUtc is { } trialEnd && trialEnd > now:
                var days = TrialThresholdDays.Where(d => now >= trialEnd.AddDays(-d)).DefaultIfEmpty(0).Min();
                if (days > 0) due.Add(new($"trial_ending_{days}", trialEnd.ToString("yyyyMMdd")));
                break;

            case SaasSubscriptionStatus.PastDue when sub.CurrentPeriodEndUtc is { } end:
                var cycle = end.ToString("yyyyMMdd");
                var sinceDays = PastDueDays.Where(d => now >= end.AddDays(d)).DefaultIfEmpty(0).Max();
                if (sinceDays > 0) due.Add(new($"past_due_day_{sinceDays}", cycle));
                if (sub.GraceEndsAtUtc is { } graceEnd && graceEnd > now)
                {
                    var hours = GraceThresholdHours.Where(h => now >= graceEnd.AddHours(-h)).DefaultIfEmpty(0).Min();
                    if (hours > 0) due.Add(new($"grace_ending_{hours}h", graceEnd.ToString("yyyyMMdd")));
                }
                break;
        }
        return due;
    }

    /// <summary>Banner shown inside the tenant's admin portal; null when nothing needs attention.</summary>
    public static (string Level, string Message)? Banner(TenantSubscription sub, DateTime now)
    {
        switch (sub.Status)
        {
            case SaasSubscriptionStatus.Trialing when sub.TrialEndsAtUtc is { } end:
                var left = Math.Max(0, (int)Math.Ceiling((end - now).TotalDays));
                return (left <= 3 ? "warning" : "info",
                    left == 0 ? "Your free trial ends today. Choose a plan to keep your workspace active."
                    : $"{left} day{(left == 1 ? "" : "s")} left in your free trial. Choose a plan to keep your workspace active.");
            case SaasSubscriptionStatus.PastDue:
                var grace = sub.GraceEndsAtUtc is { } g ? $" Your workspace will be suspended on {g:dd MMM yyyy}." : "";
                return ("danger", "Your subscription payment is overdue." + grace + " Please pay the open invoice.");
            case SaasSubscriptionStatus.Active when sub.CancelAtPeriodEnd && sub.CurrentPeriodEndUtc is { } cancelEnd:
                return ("warning", $"Your subscription is set to end on {cancelEnd:dd MMM yyyy}. You can resume it any time before then.");
        }
        return null;
    }
}
