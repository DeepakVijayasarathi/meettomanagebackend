using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.api.Marketing;

public enum CustomerHealth { Trial, New, Healthy, AtRisk, Churned }

/// <summary>CRM rules with no I/O, so they can be tested directly.</summary>
public static class CrmRules
{
    /// <summary>Chance a lead at each open stage becomes a paying customer, for the weighted forecast.</summary>
    public static double WinProbability(LeadStage stage) => stage switch
    {
        LeadStage.New => 0.05,
        LeadStage.Contacted => 0.10,
        LeadStage.DemoBooked => 0.25,
        LeadStage.DemoDone => 0.40,
        LeadStage.Trial => 0.50,
        LeadStage.ActiveTrial => 0.70,
        LeadStage.Customer => 1,
        _ => 0,
    };

    /// <summary>
    /// How an academy account is doing, with the reasons in plain words. Churned when the workspace is suspended
    /// or the subscription ended; at risk on overdue payment, a scheduled cancellation, a trial that hasn't got
    /// going or is about to end, or a paying academy with no live classes this month.
    /// </summary>
    public static (CustomerHealth Health, List<string> Reasons) Health(Tenant tenant, TenantSubscription? sub, TenantUsageSnapshot? usage, DateTime now)
    {
        var reasons = new List<string>();
        if (tenant.Status == TenantStatus.Suspended || sub?.Status is SaasSubscriptionStatus.Suspended)
            return (CustomerHealth.Churned, ["Workspace suspended"]);
        if (sub?.Status is SaasSubscriptionStatus.Expired or SaasSubscriptionStatus.Cancelled)
            return (CustomerHealth.Churned, [sub.Status == SaasSubscriptionStatus.Expired ? "Subscription expired" : "Subscription cancelled"]);

        var active = usage is { ActiveStudents: > 0, SessionsMonth: > 0 };
        var paid = (sub?.PlanPrice?.Amount ?? 0) > 0;
        if (sub?.Status == SaasSubscriptionStatus.PastDue) reasons.Add("Payment overdue");
        if (sub?.CancelAtPeriodEnd == true) reasons.Add("Cancels at the end of this period");
        if (sub?.Status == SaasSubscriptionStatus.Trialing)
        {
            if (!active && now - sub.CreatedAtUtc >= TimeSpan.FromDays(2)) reasons.Add("Trial not set up: no students or classes yet");
            if (sub.TrialEndsAtUtc is { } end && end > now && end - now <= TimeSpan.FromDays(3))
                reasons.Add($"Trial ends in {Math.Max(1, (int)Math.Ceiling((end - now).TotalDays))} day(s)");
        }
        if (sub?.Status == SaasSubscriptionStatus.Active && paid && usage is { SessionsMonth: 0 } && now.Day > 7)
            reasons.Add("No live classes this month");

        if (reasons.Count > 0) return (CustomerHealth.AtRisk, reasons);
        if (sub?.Status == SaasSubscriptionStatus.Trialing) return (CustomerHealth.Trial, ["Trial running"]);
        if (now - tenant.CreatedAtUtc < TimeSpan.FromDays(30)) return (CustomerHealth.New, ["Joined in the last 30 days"]);
        return (CustomerHealth.Healthy, active ? ["Active this month"] : []);
    }

    /// <summary>Digits only, last ten (Indian numbers with or without +91), for duplicate checks.</summary>
    public static string PhoneKey(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }
}
