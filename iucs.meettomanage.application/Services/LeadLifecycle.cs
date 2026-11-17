using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Services
{
    /// <summary>
    /// The rules of the lead lifecycle, kept free of I/O so they can be tested directly: which stage follows which,
    /// how the older status field follows the stage, when demo reminders are due and what an academy's subscription
    /// says about the lead it came from.
    /// </summary>
    public static class LeadLifecycle
    {
        /// <summary>Settings keys (app settings in the marketing workspace). Every automation is on unless switched off.</summary>
        public const string SettingPrefix = "leadAutomation.";
        public const string AutoAcknowledge = SettingPrefix + "autoAcknowledge";
        public const string AutoAssign = SettingPrefix + "autoAssign";
        public const string DemoReminders = SettingPrefix + "demoReminders";
        public const string DemoRecap = SettingPrefix + "demoRecap";
        public const string TrialSync = SettingPrefix + "trialSync";
        public const string HighIntentAlerts = SettingPrefix + "highIntentAlerts";
        /// <summary>Optional calendar link (Calendly, Google booking page) offered in the acknowledgement e-mail.</summary>
        public const string BookingUrl = SettingPrefix + "bookingUrl";

        public static readonly string[] Toggles = [AutoAcknowledge, AutoAssign, DemoReminders, DemoRecap, TrialSync, HighIntentAlerts];

        /// <summary>Activity types written by the automations, so the console can show what ran.</summary>
        public const string AutomationActivity = "Automation";

        /// <summary>Pricing-page views (after a demo or during a trial) within <see cref="HighIntentWindowDays"/> that flag a lead as ready to buy.</summary>
        public const int HighIntentPricingViews = 2;
        public const int HighIntentWindowDays = 7;

        public static bool IsOpen(LeadStage stage) => stage is not (LeadStage.Customer or LeadStage.Lost);

        /// <summary>The older four-value status the lead list and analytics still read.</summary>
        public static StoreInquiryStatus StatusFor(LeadStage stage) => stage switch
        {
            LeadStage.New => StoreInquiryStatus.New,
            LeadStage.Customer => StoreInquiryStatus.Converted,
            LeadStage.Lost => StoreInquiryStatus.Closed,
            _ => StoreInquiryStatus.Contacted,
        };

        /// <summary>The stage implied when someone changes only the older status field.</summary>
        public static LeadStage StageForStatus(StoreInquiryStatus status, LeadStage current) => status switch
        {
            StoreInquiryStatus.Converted => LeadStage.Customer,
            StoreInquiryStatus.Closed => LeadStage.Lost,
            StoreInquiryStatus.Contacted when current == LeadStage.New => LeadStage.Contacted,
            StoreInquiryStatus.New when current == LeadStage.Lost => LeadStage.New,
            _ => current,
        };

        /// <summary>Moves a lead to a stage and keeps the status, response time and stage clock in step.</summary>
        public static void Apply(PlatformDemoRequest lead, LeadStage stage, DateTime now)
        {
            if (lead.Stage == stage) return;
            lead.Stage = stage;
            lead.StageChangedAtUtc = now;
            lead.Status = StatusFor(stage);
            if (stage != LeadStage.New && lead.FirstContactedAtUtc is null) lead.FirstContactedAtUtc = now;
            if (stage != LeadStage.Lost) lead.LostReason = null;
        }

        public enum DemoReminder { None, DayBefore, HourBefore }

        /// <summary>
        /// Which reminder is due for a booked demo (StageChangedAtUtc is when it was booked or last rescheduled).
        /// The day-before reminder goes only when the demo was booked more than a day ahead, since the booking
        /// confirmation already covers shorter notice. Nothing is sent once the demo time has passed.
        /// </summary>
        public static DemoReminder ReminderDue(PlatformDemoRequest lead, DateTime now)
        {
            if (lead.Stage != LeadStage.DemoBooked || lead.DemoAtUtc is not { } at || now >= at) return DemoReminder.None;
            if (now >= at.AddHours(-1)) return lead.DemoReminderHourSentAtUtc is null ? DemoReminder.HourBefore : DemoReminder.None;
            var bookedAt = lead.StageChangedAtUtc ?? lead.CreatedAtUtc;
            if (now >= at.AddHours(-24) && lead.DemoReminderDaySentAtUtc is null && bookedAt <= at.AddHours(-24))
                return DemoReminder.DayBefore;
            return DemoReminder.None;
        }

        /// <summary>What an academy account says about its lead. Null means "no change".</summary>
        public static LeadStage? StageFromAccount(AccountSnapshot account)
        {
            if (account.IsPaidActive) return LeadStage.Customer;
            if (account.Ended) return LeadStage.Lost;
            if (account.IsTrialOrFree)
                return account.ActiveStudents > 0 && account.SessionsThisMonth > 0 ? LeadStage.ActiveTrial : LeadStage.Trial;
            return null;
        }

        /// <summary>
        /// Account-driven moves only go forward (a trial never drops a lead back from Customer), except that a lead
        /// already marked Lost by the team comes back when the academy turns out to be using or paying for the product.
        /// </summary>
        public static bool ShouldMove(LeadStage current, LeadStage next)
        {
            if (current == next) return false;
            if (next == LeadStage.Lost) return current is LeadStage.Trial or LeadStage.ActiveTrial;
            if (current == LeadStage.Lost) return next is LeadStage.ActiveTrial or LeadStage.Customer;
            return Rank(next) > Rank(current);
        }

        private static int Rank(LeadStage stage) => stage switch
        {
            LeadStage.New => 0, LeadStage.Contacted => 1, LeadStage.DemoBooked => 2, LeadStage.DemoDone => 3,
            LeadStage.Trial => 4, LeadStage.ActiveTrial => 5, LeadStage.Customer => 6, _ => -1,
        };

        /// <summary>True when the lead is at a stage where repeated pricing visits mean they are deciding.</summary>
        public static bool WatchForIntent(PlatformDemoRequest lead) =>
            lead.HighIntentAtUtc is null && lead.VisitorId is not null
            && lead.Stage is LeadStage.DemoDone or LeadStage.Trial or LeadStage.ActiveTrial;

        public static string Label(LeadStage stage) => stage switch
        {
            LeadStage.DemoBooked => "Demo booked",
            LeadStage.DemoDone => "Demo done",
            LeadStage.ActiveTrial => "Active trial",
            _ => stage.ToString(),
        };
    }

    /// <summary>The parts of an academy account (master database) the lead lifecycle needs.</summary>
    public sealed record AccountSnapshot(bool IsTrialOrFree, bool IsPaidActive, bool Ended, int ActiveStudents, int SessionsThisMonth);
}
