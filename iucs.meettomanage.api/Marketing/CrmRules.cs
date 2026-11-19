using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.api.Marketing;

public enum CustomerHealth { Trial, New, Healthy, AtRisk, Churned }

/// <summary>CRM rules with no I/O, so they can be tested directly.</summary>
public static class CrmRules
{
    /// <summary>Days without movement after which an open lead at this stage counts as stuck.</summary>
    public static int StuckDays(LeadStage stage) => stage switch
    {
        LeadStage.New => 1,
        LeadStage.Contacted => 7,
        LeadStage.DemoDone => 5,
        LeadStage.Trial => 5,
        LeadStage.ActiveTrial => 10,
        _ => int.MaxValue,
    };

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

    /// <summary>
    /// Ready-made follow-up sequences. Starting one creates a task per step on the lead's owner; open steps close
    /// on their own once the lead reaches a stop stage (a demo is booked, they buy, or they're lost).
    /// </summary>
    public static readonly IReadOnlyList<FollowUpPlan> Plans =
    [
        new("new-lead", "New enquiry (2 weeks)", "For a lead who asked about the platform: reach them fast, then keep trying for two weeks.",
            [new(0, CrmTaskType.Call, "Call: thank them, ask how they run classes and fees today, offer a demo"),
             new(1, CrmTaskType.WhatsApp, "WhatsApp: short intro and a demo link if the call didn't connect"),
             new(3, CrmTaskType.Call, "Call again: book the demo"),
             new(7, CrmTaskType.WhatsApp, "WhatsApp: share a customer story and ask for a 30-minute slot"),
             new(14, CrmTaskType.Call, "Last call: still interested? If not, mark lost with the reason")],
            [LeadStage.DemoBooked, LeadStage.DemoDone, LeadStage.Trial, LeadStage.ActiveTrial, LeadStage.Customer, LeadStage.Lost]),
        new("imported", "Imported list (1 week)", "For academies from a list who haven't asked yet: a light, friendly first touch.",
            [new(0, CrmTaskType.WhatsApp, "WhatsApp: introduce yourself and ask who handles admissions and fees"),
             new(2, CrmTaskType.Call, "Call: ask how they manage classes, fees and parent updates today"),
             new(6, CrmTaskType.WhatsApp, "WhatsApp: offer the free plan or a short demo, then stop if no reply")],
            [LeadStage.DemoBooked, LeadStage.DemoDone, LeadStage.Trial, LeadStage.ActiveTrial, LeadStage.Customer, LeadStage.Lost]),
        new("after-demo", "After the demo (1 week)", "Turn a finished demo into a trial and a decision.",
            [new(1, CrmTaskType.Call, "Call: answer questions from the demo, help them start the free trial"),
             new(3, CrmTaskType.WhatsApp, "WhatsApp: check the trial is set up; offer to import their students"),
             new(7, CrmTaskType.Call, "Call: ask for the decision, send the plan quote")],
            [LeadStage.ActiveTrial, LeadStage.Customer, LeadStage.Lost]),
        new("trial", "Trial check-ins", "Help a trial academy get going so they buy before it ends.",
            [new(1, CrmTaskType.Call, "Call: help add the first batch and students"),
             new(4, CrmTaskType.WhatsApp, "WhatsApp: tips for running the first live class and fee reminders"),
             new(10, CrmTaskType.Call, "Call: recommend a plan before the trial ends")],
            [LeadStage.Customer, LeadStage.Lost]),
    ];

    public static FollowUpPlan? Plan(string? key) => Plans.FirstOrDefault(p => p.Key == key);

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    /// <summary>
    /// When a plan step falls due: 10:00 India time, the given number of days after the start. A day-0 step that
    /// would already be past is due in 15 minutes instead, so it shows up today.
    /// </summary>
    public static DateTime StepDueUtc(DateTime startUtc, int day)
    {
        var localDay = (startUtc + Ist).Date.AddDays(day);
        var due = DateTime.SpecifyKind(localDay.AddHours(10) - Ist, DateTimeKind.Utc);
        return due < startUtc ? startUtc.AddMinutes(15) : due;
    }

    /// <summary>Digits only, last ten (Indian numbers with or without +91), for duplicate checks.</summary>
    public static string PhoneKey(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }
}

public sealed record FollowUpStep(int Day, iucs.meettomanage.domain.Entities.Marketing.CrmTaskType Type, string Title);
public sealed record FollowUpPlan(string Key, string Name, string Description, FollowUpStep[] Steps, LeadStage[] StopAt);
