using iucs.meettomanage.api.Marketing;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Helper;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class CrmTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakeCurrentUser _me = new();

    public void Dispose() => _db.Dispose();

    private CrmService Crm() => new(_db.Context, new CrmAccess(_db.Context, _me), new BcryptPasswordHasher(), new ServiceCollection().BuildServiceProvider());

    private async Task<User> MarketingUser(string first, CrmRole? role)
    {
        var user = new User { Id = Guid.NewGuid(), Email = $"{first.ToLowerInvariant()}@crm.test", FirstName = first, LastName = "T", PinHash = "x", Role = UserRole.Marketing };
        _db.Context.Users.Add(user);
        if (role is { } r) _db.Context.CrmTeamMembers.Add(new CrmTeamMember { Id = Guid.NewGuid(), UserId = user.Id, Role = r });
        await _db.Context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task MarketingUserWithoutARow_IsAManager_AndCanAddSalesAndViewers()
    {
        var boss = await MarketingUser("Boss", null);
        _me.UserId = boss.Id;
        Assert.Equal(CrmRole.Manager, (await Crm().MeAsync(default)).Role);

        var rep = await Crm().AddMemberAsync(new AddCrmMemberRequest { FirstName = "Ravi", Email = "Ravi@Crm.test", Pin = "1234", Role = CrmRole.Sales }, default);
        Assert.Equal("ravi@crm.test", rep.Email);
        await Assert.ThrowsAsync<DomainValidationException>(() => Crm().AddMemberAsync(new AddCrmMemberRequest { FirstName = "X", Email = "x@crm.test", Pin = "12", Role = CrmRole.Sales }, default));
        await Assert.ThrowsAsync<ConflictException>(() => Crm().AddMemberAsync(new AddCrmMemberRequest { FirstName = "R", Email = "ravi@crm.test", Pin = "1234" }, default));

        // The only manager can't demote or deactivate themselves.
        await Assert.ThrowsAsync<DomainValidationException>(() => Crm().UpdateMemberAsync(boss.Id, new UpdateCrmMemberRequest { Role = CrmRole.Viewer }, default));
        await Assert.ThrowsAsync<DomainValidationException>(() => Crm().UpdateMemberAsync(boss.Id, new UpdateCrmMemberRequest { Active = false }, default));

        _me.UserId = rep.UserId;
        await Assert.ThrowsAsync<ForbiddenException>(() => Crm().AddMemberAsync(new AddCrmMemberRequest { FirstName = "Y", Email = "y@crm.test", Pin = "1234" }, default));
    }

    [Fact]
    public async Task Import_SkipsDuplicatesAndBadRows_AndSendsNothing()
    {
        _me.UserId = (await MarketingUser("Mgr", CrmRole.Manager)).Id;
        _db.Context.PlatformDemoRequests.Add(new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "Old", WorkEmail = "old@a.in", Phone = "+91 98765 43210", AcademyName = "Old Academy" });
        await _db.Context.SaveChangesAsync();

        var result = await Crm().ImportLeadsAsync(new ImportCrmLeadsRequest
        {
            Source = "week1 list",
            Rows =
            [
                new() { FullName = "Deepa", Email = "deepa@a.in", AcademyName = "Bachpan", City = "Bengaluru", Region = "Bengaluru", Segment = "Phonics" },
                new() { AcademyName = "Arivagam", Phone = "94454 11121" },                  // name falls back to the academy
                new() { FullName = "Dup", Email = "DEEPA@a.in" },                           // same e-mail as row 0
                new() { FullName = "Dup phone", Phone = "9876543210" },                     // same phone as the existing lead
                new() { FullName = "No contact" },
                new() { FullName = "Bad", Email = "not-an-email" },
            ],
        }, default);

        Assert.Equal(2, result.Created);
        Assert.Equal([2, 3, 4, 5], result.Skipped.Select(s => s.Row).ToArray());
        var deepa = await _db.Context.PlatformDemoRequests.SingleAsync(x => x.WorkEmail == "deepa@a.in");
        Assert.Equal(("Bengaluru", "Phonics", "week1 list", LeadStage.New), (deepa.City, deepa.Segment, deepa.Source, deepa.Stage));
        Assert.Null(deepa.AcknowledgedAtUtc);
        Assert.True(await _db.Context.PlatformDemoRequests.AnyAsync(x => x.FullName == "Arivagam team"));
    }

    [Fact]
    public async Task SalesRep_SeesOwnAndUnassignedLeads_Only_AndViewerCannotWrite()
    {
        var rep = await MarketingUser("Rep", CrmRole.Sales);
        var other = await MarketingUser("Other", CrmRole.Sales);
        var viewer = await MarketingUser("View", CrmRole.Viewer);
        var mine = new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "Mine", WorkEmail = "m@a.in", Phone = "1", AcademyName = "A", AssignedToUserId = rep.Id };
        var theirs = new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "Theirs", WorkEmail = "t@a.in", Phone = "2", AcademyName = "B", AssignedToUserId = other.Id };
        var open = new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "Open", WorkEmail = "o@a.in", Phone = "3", AcademyName = "C" };
        _db.Context.PlatformDemoRequests.AddRange(mine, theirs, open);
        await _db.Context.SaveChangesAsync();

        _me.UserId = rep.Id;
        var access = new CrmAccess(_db.Context, _me);
        await access.EnsureCanSeeLeadAsync(mine.Id);
        await access.EnsureCanSeeLeadAsync(open.Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => access.EnsureCanSeeLeadAsync(theirs.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => Crm().SaveTaskAsync(null, new SaveCrmTaskRequest { Title = "Call", DueAtUtc = DateTime.UtcNow, LeadId = mine.Id, AssignedToUserId = other.Id }, default));

        // Logging a call moves a new lead to Contacted and schedules the next step on the rep.
        var logged = await Crm().LogInteractionAsync(mine.Id, new LogCrmInteractionRequest { Type = "Call", Outcome = "Interested", NextStepTitle = "Send pricing", NextStepDueAtUtc = DateTime.UtcNow.AddDays(1) }, default);
        Assert.Equal(LeadStage.Contacted, logged.Stage);
        Assert.NotNull(logged.FirstContactedAtUtc);
        var task = Assert.Single(await Crm().TasksAsync("mine", "open", null, null, default));
        Assert.Equal(("Send pricing", rep.Id), (task.Title, task.AssignedToUserId));

        _me.UserId = viewer.Id;
        var viewerAccess = new CrmAccess(_db.Context, _me);
        await viewerAccess.EnsureCanSeeLeadAsync(theirs.Id); // viewers read everything
        await Assert.ThrowsAsync<ForbiddenException>(() => Crm().LogInteractionAsync(mine.Id, new LogCrmInteractionRequest { Type = "Call", Outcome = "No answer" }, default));
    }

    [Fact]
    public async Task Forecast_WeightsOpenDealsByStage()
    {
        _me.UserId = (await MarketingUser("Mgr", CrmRole.Manager)).Id;
        _db.Context.PlatformDemoRequests.AddRange(
            new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "A", WorkEmail = "", Phone = "1", AcademyName = "A", Stage = LeadStage.DemoDone, ExpectedMonthlyValue = 2499 },
            new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "B", WorkEmail = "", Phone = "2", AcademyName = "B", Stage = LeadStage.ActiveTrial, ExpectedMonthlyValue = 5999 },
            new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "C", WorkEmail = "", Phone = "3", AcademyName = "C", Stage = LeadStage.New });
        await _db.Context.SaveChangesAsync();

        var report = await Crm().ReportAsync(90, default);
        Assert.Equal(Math.Round(2499m * 0.40m + 5999m * 0.70m, 0), report.WeightedPipeline);
        Assert.Equal(1, report.OpenWithoutValue);
        Assert.Equal(3, report.Owners.Single(o => o.OwnerId is null).OpenLeads);
    }

    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
    private static Tenant TenantOld => new() { Name = "A", Code = "a", DatabaseName = "mt_a", Status = TenantStatus.Active, CreatedAtUtc = Now.AddDays(-200) };

    [Fact]
    public void CustomerHealth_FlagsRisksInPlainWords()
    {
        var paid = new TenantSubscription { Status = SaasSubscriptionStatus.Active, PlanPrice = new PlanPrice { Amount = 2499 }, CreatedAtUtc = Now.AddDays(-150) };
        Assert.Equal(CustomerHealth.Healthy, CrmRules.Health(TenantOld, paid, new TenantUsageSnapshot { ActiveStudents = 40, SessionsMonth = 9 }, Now).Health);

        var (h, why) = CrmRules.Health(TenantOld, paid, new TenantUsageSnapshot { ActiveStudents = 40, SessionsMonth = 0 }, Now);
        Assert.Equal(CustomerHealth.AtRisk, h);
        Assert.Contains("No live classes this month", why);

        paid.Status = SaasSubscriptionStatus.PastDue;
        Assert.Contains("Payment overdue", CrmRules.Health(TenantOld, paid, null, Now).Reasons);

        var trial = new TenantSubscription { Status = SaasSubscriptionStatus.Trialing, CreatedAtUtc = Now.AddDays(-12), TrialEndsAtUtc = Now.AddDays(2) };
        var (th, treasons) = CrmRules.Health(TenantOld, trial, null, Now);
        Assert.Equal(CustomerHealth.AtRisk, th);
        Assert.Equal(["Trial not set up: no students or classes yet", "Trial ends in 2 day(s)"], treasons);

        Assert.Equal(CustomerHealth.Churned, CrmRules.Health(TenantOld, new TenantSubscription { Status = SaasSubscriptionStatus.Expired }, null, Now).Health);
        var fresh = TenantOld; fresh.CreatedAtUtc = Now.AddDays(-5);
        Assert.Equal(CustomerHealth.New, CrmRules.Health(fresh, new TenantSubscription { Status = SaasSubscriptionStatus.Active }, null, Now).Health);
    }

    [Theory]
    [InlineData("+91 98765 43210", "9876543210")]
    [InlineData("098765-43210", "9876543210")]
    [InlineData("12345", "12345")]
    public void PhoneKeyComparesTheLastTenDigits(string phone, string key) => Assert.Equal(key, CrmRules.PhoneKey(phone));
}
