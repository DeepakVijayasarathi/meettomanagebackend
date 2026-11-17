using iucs.meettomanage.api.Marketing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Enums;
using Xunit;

namespace iucs.meettomanage.tests;

public class LeadLifecycleTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);

    private static PlatformDemoRequest Lead(LeadStage stage = LeadStage.New, Action<PlatformDemoRequest>? tweak = null)
    {
        var lead = new PlatformDemoRequest { FullName = "A", WorkEmail = "a@b.co", Phone = "1", AcademyName = "X", Stage = stage, CreatedAtUtc = Now.AddDays(-5) };
        tweak?.Invoke(lead);
        return lead;
    }

    [Theory]
    [InlineData(LeadStage.New, StoreInquiryStatus.New)]
    [InlineData(LeadStage.DemoBooked, StoreInquiryStatus.Contacted)]
    [InlineData(LeadStage.ActiveTrial, StoreInquiryStatus.Contacted)]
    [InlineData(LeadStage.Customer, StoreInquiryStatus.Converted)]
    [InlineData(LeadStage.Lost, StoreInquiryStatus.Closed)]
    public void StatusFollowsTheStage(LeadStage stage, StoreInquiryStatus status) => Assert.Equal(status, LeadLifecycle.StatusFor(stage));

    [Fact]
    public void ChangingOnlyTheStatusNeverDropsAnAdvancedStage()
    {
        Assert.Equal(LeadStage.Contacted, LeadLifecycle.StageForStatus(StoreInquiryStatus.Contacted, LeadStage.New));
        Assert.Equal(LeadStage.Trial, LeadLifecycle.StageForStatus(StoreInquiryStatus.Contacted, LeadStage.Trial));
        Assert.Equal(LeadStage.Customer, LeadLifecycle.StageForStatus(StoreInquiryStatus.Converted, LeadStage.DemoDone));
        Assert.Equal(LeadStage.New, LeadLifecycle.StageForStatus(StoreInquiryStatus.New, LeadStage.Lost));
    }

    [Fact]
    public void ApplySetsFirstContactOnce_AndClearsTheLostReasonWhenReopened()
    {
        var lead = Lead();
        LeadLifecycle.Apply(lead, LeadStage.Contacted, Now);
        LeadLifecycle.Apply(lead, LeadStage.DemoBooked, Now.AddDays(1));
        Assert.Equal(Now, lead.FirstContactedAtUtc);
        Assert.Equal(Now.AddDays(1), lead.StageChangedAtUtc);
        lead.Stage = LeadStage.Lost; lead.LostReason = "Price";
        LeadLifecycle.Apply(lead, LeadStage.Contacted, Now.AddDays(2));
        Assert.Null(lead.LostReason);
    }

    [Fact]
    public void DemoReminders_DayBeforeThenHourBefore_ThenNothingAfterTheDemo()
    {
        var lead = Lead(LeadStage.DemoBooked, l => { l.DemoAtUtc = Now.AddHours(20); l.StageChangedAtUtc = Now.AddDays(-3); });
        Assert.Equal(LeadLifecycle.DemoReminder.DayBefore, LeadLifecycle.ReminderDue(lead, Now));
        lead.DemoReminderDaySentAtUtc = Now;
        Assert.Equal(LeadLifecycle.DemoReminder.None, LeadLifecycle.ReminderDue(lead, Now.AddHours(1)));
        Assert.Equal(LeadLifecycle.DemoReminder.HourBefore, LeadLifecycle.ReminderDue(lead, Now.AddHours(19.5)));
        lead.DemoReminderHourSentAtUtc = Now.AddHours(19.5);
        Assert.Equal(LeadLifecycle.DemoReminder.None, LeadLifecycle.ReminderDue(lead, Now.AddHours(19.7)));
        Assert.Equal(LeadLifecycle.DemoReminder.None, LeadLifecycle.ReminderDue(lead, Now.AddHours(21)));
    }

    [Fact]
    public void DemoBookedForTomorrowGetsNoDayBeforeReminder_OnlyTheHourBefore()
    {
        var lead = Lead(LeadStage.DemoBooked, l => { l.DemoAtUtc = Now.AddHours(18); l.StageChangedAtUtc = Now; });
        Assert.Equal(LeadLifecycle.DemoReminder.None, LeadLifecycle.ReminderDue(lead, Now.AddMinutes(10)));
        Assert.Equal(LeadLifecycle.DemoReminder.HourBefore, LeadLifecycle.ReminderDue(lead, Now.AddHours(17.2)));
    }

    [Fact]
    public void NoRemindersUnlessTheDemoIsStillBooked() =>
        Assert.Equal(LeadLifecycle.DemoReminder.None, LeadLifecycle.ReminderDue(Lead(LeadStage.DemoDone, l => l.DemoAtUtc = Now.AddMinutes(30)), Now));

    private static TenantSubscription Sub(SaasSubscriptionStatus status, decimal price) =>
        new() { Status = status, PlanPrice = price > 0 ? new PlanPrice { Amount = price } : null };

    [Fact]
    public void AccountStateMapsToStages()
    {
        var idle = new TenantUsageSnapshot { ActiveStudents = 0, SessionsMonth = 0 };
        var busy = new TenantUsageSnapshot { ActiveStudents = 12, SessionsMonth = 3 };
        Assert.Equal(LeadStage.Trial, LeadLifecycle.StageFromAccount(LeadAutomationService.Account(Sub(SaasSubscriptionStatus.Trialing, 0), idle)));
        Assert.Equal(LeadStage.Trial, LeadLifecycle.StageFromAccount(LeadAutomationService.Account(Sub(SaasSubscriptionStatus.Trialing, 0), null)));
        Assert.Equal(LeadStage.ActiveTrial, LeadLifecycle.StageFromAccount(LeadAutomationService.Account(Sub(SaasSubscriptionStatus.Active, 0), busy)));
        Assert.Equal(LeadStage.Customer, LeadLifecycle.StageFromAccount(LeadAutomationService.Account(Sub(SaasSubscriptionStatus.Active, 2499), idle)));
        Assert.Equal(LeadStage.Lost, LeadLifecycle.StageFromAccount(LeadAutomationService.Account(Sub(SaasSubscriptionStatus.Expired, 0), null)));
        Assert.Null(LeadLifecycle.StageFromAccount(LeadAutomationService.Account(Sub(SaasSubscriptionStatus.Suspended, 0), null)));
    }

    [Theory]
    [InlineData(LeadStage.DemoDone, LeadStage.Trial, true)]
    [InlineData(LeadStage.Trial, LeadStage.ActiveTrial, true)]
    [InlineData(LeadStage.Customer, LeadStage.Trial, false)]      // never backwards
    [InlineData(LeadStage.DemoBooked, LeadStage.Lost, false)]     // the team decides when an open lead is lost
    [InlineData(LeadStage.ActiveTrial, LeadStage.Lost, true)]     // a trial that ended unpaid
    [InlineData(LeadStage.Lost, LeadStage.Trial, false)]
    [InlineData(LeadStage.Lost, LeadStage.Customer, true)]        // came back and paid
    public void AccountMovesOnlyGoForward(LeadStage from, LeadStage to, bool moves) => Assert.Equal(moves, LeadLifecycle.ShouldMove(from, to));
}
