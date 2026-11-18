using iucs.meettomanage.api.Marketing;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Helper;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    /// <summary>Runs the lead-endpoint role filter for one action and reports whether the action itself was reached.</summary>
    private async Task<bool> Through(string action, string method, Dictionary<string, object?> args)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Method = method;
        var descriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor { ActionName = action };
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), descriptor);
        var context = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(actionContext, [], args, controller: new object());
        var reached = false;
        await new CrmLeadAccessFilter(new CrmAccess(_db.Context, _me)).OnActionExecutionAsync(context, () =>
        {
            reached = true;
            return Task.FromResult(new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(actionContext, [], new object()));
        });
        return reached;
    }

    [Fact]
    public async Task LeadEndpoints_FollowCrmRoles()
    {
        var rep = await MarketingUser("Rep", CrmRole.Sales);
        var other = await MarketingUser("Other", CrmRole.Sales);
        var viewer = await MarketingUser("View", CrmRole.Viewer);
        var mgr = await MarketingUser("Mgr", CrmRole.Manager);
        var theirs = new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "T", WorkEmail = "t@a.in", Phone = "2", AcademyName = "B", AssignedToUserId = other.Id };
        var mine = new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "M", WorkEmail = "m@a.in", Phone = "3", AcademyName = "C", AssignedToUserId = rep.Id };
        _db.Context.PlatformDemoRequests.AddRange(theirs, mine);
        await _db.Context.SaveChangesAsync();

        // Sales rep: the list is narrowed server-side, even if the client sends someone else's id.
        _me.UserId = rep.Id;
        var query = new application.Dto.Marketing.MarketingLeadQuery { VisibleToUserId = other.Id };
        Assert.True(await Through("ListPaged", "GET", new() { ["query"] = query }));
        Assert.Equal(rep.Id, query.VisibleToUserId);
        Assert.True(await Through("Get", "GET", new() { ["id"] = mine.Id }));
        await Assert.ThrowsAsync<ForbiddenException>(() => Through("Get", "GET", new() { ["id"] = theirs.Id }));
        await Assert.ThrowsAsync<ForbiddenException>(() => Through("SetStage", "PUT", new() { ["id"] = theirs.Id }));
        await Assert.ThrowsAsync<ForbiddenException>(() => Through("List", "GET", new()));
        await Assert.ThrowsAsync<ForbiddenException>(() => Through("Delete", "DELETE", new() { ["id"] = mine.Id }));
        await Assert.ThrowsAsync<ForbiddenException>(() => Through("SaveSettings", "PUT", new()));
        await Assert.ThrowsAsync<ForbiddenException>(() => Through("Update", "PUT", new() { ["id"] = mine.Id, ["request"] = new application.Dto.Marketing.UpdatePlatformDemoRequestRequest { AssignedToUserId = other.Id } }));
        Assert.True(await Through("Update", "PUT", new() { ["id"] = mine.Id, ["request"] = new application.Dto.Marketing.UpdatePlatformDemoRequestRequest { AssignedToUserId = rep.Id } }));
        await Assert.ThrowsAsync<ForbiddenException>(() => Through("Bulk", "POST", new() { ["request"] = new application.Dto.Marketing.BulkMarketingLeadRequest { Ids = [mine.Id, theirs.Id] } }));

        // Viewer: reads everything, changes nothing; the query is not narrowed.
        _me.UserId = viewer.Id;
        var viewerQuery = new application.Dto.Marketing.MarketingLeadQuery { VisibleToUserId = rep.Id };
        Assert.True(await Through("ListPaged", "GET", new() { ["query"] = viewerQuery }));
        Assert.Null(viewerQuery.VisibleToUserId);
        Assert.True(await Through("Get", "GET", new() { ["id"] = theirs.Id }));
        await Assert.ThrowsAsync<ForbiddenException>(() => Through("UpdateStatus", "PUT", new() { ["id"] = theirs.Id }));

        // Manager: everything.
        _me.UserId = mgr.Id;
        Assert.True(await Through("Delete", "DELETE", new() { ["id"] = theirs.Id }));
        Assert.True(await Through("List", "GET", new()));
    }

    [Fact]
    public async Task NewWebsiteLeads_AreNeverAutoAssignedToAViewer()
    {
        var viewer = await MarketingUser("Aaron", CrmRole.Viewer);   // alphabetically first, no open leads
        var rep = await MarketingUser("Zara", CrmRole.Sales);
        var demos = new application.Services.PlatformDemoRequestService(_db.UnitOfWork, new application.Services.AuditLogService(_db.UnitOfWork, _db.CurrentUser));
        var lead = await demos.CreateAsync(new application.Dto.Marketing.CreatePlatformDemoRequestRequest { FullName = "Web", WorkEmail = "web@a.in", Phone = "9000000500", AcademyName = "Web Academy" });
        Assert.Equal(rep.Id, lead.AssignedToUserId);
        Assert.NotEqual(viewer.Id, lead.AssignedToUserId);
    }

    [Fact]
    public async Task TaskOnALeadWhoseOwnerLeft_GoesToWhoeverAddsIt()
    {
        var gone = await MarketingUser("Gone", CrmRole.Sales);
        var mgr = await MarketingUser("Mgr", CrmRole.Manager);
        var lead = new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "L", WorkEmail = "l@a.in", Phone = "7", AcademyName = "L", AssignedToUserId = gone.Id };
        _db.Context.PlatformDemoRequests.Add(lead);
        (await _db.Context.Users.FirstAsync(x => x.Id == gone.Id)).Status = UserStatus.Inactive;
        await _db.Context.SaveChangesAsync();
        _me.UserId = mgr.Id;
        var task = await Crm().SaveTaskAsync(null, new SaveCrmTaskRequest { Title = "Pick up", DueAtUtc = DateTime.UtcNow, LeadId = lead.Id }, default);
        Assert.Equal(mgr.Id, task.AssignedToUserId);
        await Assert.ThrowsAsync<DomainValidationException>(() => Crm().SaveTaskAsync(null, new SaveCrmTaskRequest { Title = "x", DueAtUtc = DateTime.UtcNow, AssignedToUserId = gone.Id }, default));
    }

    [Fact]
    public async Task AutomationOverview_ShowsASalesRepOnlyTheirLeads()
    {
        var rep = await MarketingUser("Rep", CrmRole.Sales);
        var other = await MarketingUser("Other", CrmRole.Sales);
        var soon = DateTime.UtcNow.AddDays(1);
        _db.Context.PlatformDemoRequests.AddRange(
            new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "Mine", WorkEmail = "", Phone = "1", AcademyName = "Mine", AssignedToUserId = rep.Id, Stage = LeadStage.DemoBooked, DemoAtUtc = soon },
            new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "Theirs", WorkEmail = "", Phone = "2", AcademyName = "Theirs", AssignedToUserId = other.Id, Stage = LeadStage.DemoBooked, DemoAtUtc = soon });
        await _db.Context.SaveChangesAsync();
        var automation = new LeadAutomationService(_db.Context, new FakeEmailSender(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new ServiceCollection().BuildServiceProvider(), Microsoft.Extensions.Logging.Abstractions.NullLogger<LeadAutomationService>.Instance);
        Assert.Equal(["Mine"], (await automation.OverviewAsync(30, default, rep.Id)).UpcomingDemos.Select(x => x.AcademyName).ToArray());
        Assert.Equal(2, (await automation.OverviewAsync(30, default)).UpcomingDemos.Count);
    }

    private sealed class MasterFactory(DbContextOptions<domain.Data.MasterDbContext> options) : IDbContextFactory<domain.Data.MasterDbContext>
    {
        public domain.Data.MasterDbContext CreateDbContext() => new(options);
    }

    private const string OwnerMarketingEmail = "marketing@platform.test";
    private static readonly Guid OwnerTenantId = Guid.NewGuid();

    /// <summary>A CRM bound to <paramref name="tenantId"/>, where the platform owner (holder of Seed:MarketingEmail) is <see cref="OwnerTenantId"/>.</summary>
    private CrmService TenantCrm(DbContextOptions<domain.Data.MasterDbContext> options, Guid tenantId)
    {
        var tenant = new iucs.meettomanage.api.Tenancy.TenantContext();
        tenant.Bind(tenantId, tenantId == OwnerTenantId ? "owner" : "other", "Host=unused;Database=mt_test");
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:MarketingEmail"] = OwnerMarketingEmail }).Build();
        var services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<domain.Data.MasterDbContext>>(new MasterFactory(options))
            .AddSingleton(tenant)
            .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration)
            .BuildServiceProvider();
        return new CrmService(_db.Context, new CrmAccess(_db.Context, _me), new BcryptPasswordHasher(), services);
    }

    private static void AddOwnerTenant(domain.Data.MasterDbContext master)
    {
        var userId = Guid.NewGuid();
        master.Tenants.Add(new Tenant { Id = OwnerTenantId, Code = "owner", Name = "Platform Owner", DatabaseName = "mt_owner", Status = TenantStatus.Active });
        master.LoginAccounts.Add(new TenantLoginAccount { UserId = userId, TenantId = OwnerTenantId });
        master.LoginIdentifiers.Add(new TenantLoginIdentifier { Identifier = "email:" + OwnerMarketingEmail, UserId = userId, TenantId = OwnerTenantId });
    }

    [Fact]
    public async Task Customers_AreHiddenFromAMarketingRoleOutsideThePlatformOwnersTenant()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<domain.Data.MasterDbContext>().UseSqlite(connection).Options;
        var otherId = Guid.NewGuid();
        await using (var master = new domain.Data.MasterDbContext(options))
        {
            await master.Database.EnsureCreatedAsync();
            AddOwnerTenant(master);
            master.Tenants.Add(new Tenant { Id = otherId, Code = "other", Name = "Other Academy", DatabaseName = "mt_other", Status = TenantStatus.Active });
            master.TenantBillingProfiles.Add(new TenantBillingProfile { TenantId = OwnerTenantId, BillingEmail = "billing@owner.test" });
            await master.SaveChangesAsync();
        }

        // e.g. a Marketing user carried over in an imported legacy database
        _me.UserId = (await MarketingUser("Imported", CrmRole.Manager)).Id;
        var crm = TenantCrm(options, otherId);

        Assert.False(await crm.CustomersAvailableAsync(default));
        Assert.Empty(await crm.CustomersAsync(null, null, default));
        await Assert.ThrowsAsync<NotFoundException>(() => crm.CustomerAsync(OwnerTenantId, default));
        await Assert.ThrowsAsync<NotFoundException>(() => crm.AddCustomerNoteAsync(OwnerTenantId, "probe", default));
        Assert.True(await TenantCrm(options, OwnerTenantId).CustomersAvailableAsync(default));
    }

    [Fact]
    public async Task Customers_ReadFromTheMasterDatabase_WithHealthInvoicesAndNotes()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<domain.Data.MasterDbContext>().UseSqlite(connection).Options;
        var paidId = Guid.NewGuid();
        var trialId = Guid.NewGuid();
        await using (var master = new domain.Data.MasterDbContext(options))
        {
            await master.Database.EnsureCreatedAsync();
            AddOwnerTenant(master); // the platform owner itself is not listed as a customer
            var plan = new Plan { Code = "pro", Name = "Pro" };
            var price = new PlanPrice { Plan = plan, BillingInterval = BillingInterval.Year, Amount = 24_990 };
            master.Plans.Add(plan);
            master.Tenants.AddRange(
                new Tenant { Id = paidId, Code = "bright", Name = "Bright Abacus", DatabaseName = "mt_a", Status = TenantStatus.Active, CreatedAtUtc = DateTime.UtcNow.AddDays(-200) },
                new Tenant { Id = trialId, Code = "newbie", Name = "Newbie Phonics", DatabaseName = "mt_b", Status = TenantStatus.Active, CreatedAtUtc = DateTime.UtcNow.AddDays(-5) });
            master.TenantSubscriptions.AddRange(
                new TenantSubscription { TenantId = paidId, Plan = plan, PlanPrice = price, Status = SaasSubscriptionStatus.PastDue, CreatedAtUtc = DateTime.UtcNow.AddDays(-200) },
                new TenantSubscription { TenantId = trialId, Plan = plan, Status = SaasSubscriptionStatus.Trialing, CreatedAtUtc = DateTime.UtcNow.AddDays(-5), TrialEndsAtUtc = DateTime.UtcNow.AddDays(9) });
            master.TenantUsageSnapshots.Add(new TenantUsageSnapshot { TenantId = paidId, SnapshotDate = DateOnly.FromDateTime(DateTime.UtcNow), ActiveStudents = 80, SessionsMonth = 12 });
            master.TenantBillingProfiles.Add(new TenantBillingProfile { TenantId = paidId, BillingEmail = "owner@bright.in", BillingPhone = "900" });
            master.PlatformInvoices.Add(new PlatformInvoice { TenantId = paidId, InvoiceNumber = "INV-1", Total = 24_990, Status = PlatformInvoiceStatus.Open, DueAtUtc = DateTime.UtcNow.AddDays(-3) });
            await master.SaveChangesAsync();
        }

        _me.UserId = (await MarketingUser("Mgr", CrmRole.Manager)).Id;
        var crm = TenantCrm(options, OwnerTenantId);

        var all = await crm.CustomersAsync(null, null, default);
        Assert.Equal(["Bright Abacus", "Newbie Phonics"], all.Select(c => c.Name).ToArray());   // at risk first
        var bright = all[0];
        Assert.Equal((CustomerHealth.AtRisk, 2083m, 80), (bright.Health, bright.MonthlyValue, bright.Students!.Value));
        Assert.Contains("Payment overdue", bright.HealthReasons);
        Assert.Single(await crm.CustomersAsync("newbie", null, default));
        // The 5-day-old trial has no students or classes yet, so it's at risk rather than a plain trial.
        Assert.Equal(2, (await crm.CustomersAsync(null, CustomerHealth.AtRisk, default)).Count);
        Assert.Contains("Trial not set up: no students or classes yet", all[1].HealthReasons);

        await crm.AddCustomerNoteAsync(paidId, "Asked about a second branch", default);
        await crm.SaveTaskAsync(null, new SaveCrmTaskRequest { Title = "Chase invoice", DueAtUtc = DateTime.UtcNow.AddDays(1), TenantId = paidId }, default);
        var detail = await crm.CustomerAsync(paidId, default);
        Assert.Equal("INV-1", Assert.Single(detail.Invoices).Number);
        Assert.Equal("Asked about a second branch", Assert.Single(detail.Notes).Text);
        var task = Assert.Single(detail.Tasks);
        Assert.Equal("Bright Abacus", task.CustomerName);
        Assert.Equal("900", detail.Phone);
    }

    [Fact]
    public async Task MyDay_SplitsTasksAndFindsDemosAndWaitingLeads()
    {
        var rep = await MarketingUser("Rep", CrmRole.Sales);
        _me.UserId = rep.Id;
        var now = DateTime.UtcNow;
        _db.Context.PlatformDemoRequests.AddRange(
            new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "Demo", WorkEmail = "", Phone = "1", AcademyName = "Demo Today", AssignedToUserId = rep.Id, Stage = LeadStage.DemoBooked, DemoAtUtc = now.AddMinutes(30) },
            new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "New", WorkEmail = "", Phone = "2", AcademyName = "Waiting", Stage = LeadStage.New });
        _db.Context.CrmTasks.AddRange(
            new CrmTask { Id = Guid.NewGuid(), Title = "Late", DueAtUtc = now.AddDays(-2), AssignedToUserId = rep.Id },
            new CrmTask { Id = Guid.NewGuid(), Title = "Next week", DueAtUtc = now.AddDays(3), AssignedToUserId = rep.Id },
            new CrmTask { Id = Guid.NewGuid(), Title = "Done", DueAtUtc = now.AddDays(-1), AssignedToUserId = rep.Id, CompletedAtUtc = now });
        await _db.Context.SaveChangesAsync();

        var day = await Crm().TodayAsync(0, default);
        Assert.Equal(["Late"], day.Overdue.Select(t => t.Title).ToArray());
        Assert.Equal(["Next week"], day.Upcoming.Select(t => t.Title).ToArray());
        Assert.Contains(day.WaitingForReply, l => l.AcademyName == "Waiting");
        if (now.AddMinutes(30).Date == now.Date) Assert.Equal("Demo Today", Assert.Single(day.DemosToday).AcademyName);
    }

    [Fact]
    public async Task Import_SplitsEvenlyBetweenOwners_AndStartsTheFollowUpPlan()
    {
        _me.UserId = (await MarketingUser("Mgr", CrmRole.Manager)).Id;
        var asha = await MarketingUser("Asha", CrmRole.Sales);
        var ravi = await MarketingUser("Ravi", CrmRole.Sales);
        var rows = Enumerable.Range(1, 5).Select(i => new CrmLeadRow { AcademyName = $"Academy {i}", Phone = $"98000000{i:D2}" }).ToList();

        var result = await Crm().ImportLeadsAsync(new ImportCrmLeadsRequest { Rows = rows, OwnerUserIds = [asha.Id, ravi.Id], FollowUpPlan = "imported" }, default);

        Assert.Equal(5, result.Created);
        var leads = await _db.Context.PlatformDemoRequests.Where(x => result.CreatedIds.Contains(x.Id)).ToListAsync();
        Assert.Equal(3, leads.Count(x => x.AssignedToUserId == asha.Id));
        Assert.Equal(2, leads.Count(x => x.AssignedToUserId == ravi.Id));
        var tasks = await _db.Context.CrmTasks.Where(x => x.PlanKey == "imported").ToListAsync();
        Assert.Equal(5 * CrmRules.Plan("imported")!.Steps.Length, tasks.Count);
        Assert.All(tasks, t => Assert.Equal(leads.Single(l => l.Id == t.LeadId).AssignedToUserId, t.AssignedToUserId));
        Assert.All(leads, l => Assert.NotNull(l.NextFollowUpAtUtc));
        await Assert.ThrowsAsync<DomainValidationException>(() => Crm().ImportLeadsAsync(new ImportCrmLeadsRequest { Rows = [new() { AcademyName = "X", Phone = "9111111111" }], FollowUpPlan = "nope" }, default));
    }

    [Fact]
    public async Task FollowUpPlan_SkipsDuplicatesAndClosedLeads_AndStopsWhenTheLeadMovesOn()
    {
        var rep = await MarketingUser("Rep", CrmRole.Sales);
        _me.UserId = rep.Id;
        var open = new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "Open", WorkEmail = "", Phone = "1", AcademyName = "Open", AssignedToUserId = rep.Id };
        var won = new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "Won", WorkEmail = "", Phone = "2", AcademyName = "Won", AssignedToUserId = rep.Id, Stage = LeadStage.Customer };
        _db.Context.PlatformDemoRequests.AddRange(open, won);
        await _db.Context.SaveChangesAsync();

        var first = await Crm().StartPlanAsync([open.Id, won.Id], "new-lead", default);
        Assert.Equal(1, first.Done);
        Assert.Contains(first.Skipped, s => s.StartsWith("Won"));
        var again = await Crm().StartPlanAsync([open.Id], "new-lead", default);
        Assert.Equal(0, again.Done);   // already running

        var steps = await _db.Context.CrmTasks.Where(x => x.LeadId == open.Id).OrderBy(x => x.DueAtUtc).ToListAsync();
        Assert.Equal(CrmRules.Plan("new-lead")!.Steps.Select(s => s.Type), steps.Select(s => s.Type));

        // Booking the demo ends the chase: the open steps close with the reason.
        (await _db.Context.PlatformDemoRequests.FirstAsync(x => x.Id == open.Id)).Stage = LeadStage.DemoBooked;
        await _db.Context.SaveChangesAsync();
        var automation = new LeadAutomationService(_db.Context, new FakeEmailSender(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new ServiceCollection().BuildServiceProvider(), Microsoft.Extensions.Logging.Abstractions.NullLogger<LeadAutomationService>.Instance);
        Assert.Equal(steps.Count, await automation.StopFinishedPlansAsync(DateTime.UtcNow, default));
        Assert.All(await _db.Context.CrmTasks.Where(x => x.LeadId == open.Id).ToListAsync(), t =>
        {
            Assert.NotNull(t.CompletedAtUtc);
            Assert.StartsWith("Stopped automatically", t.Outcome);
        });
    }

    [Fact]
    public async Task BulkAssign_IsForManagers_AndMovesOpenTasksWithTheLead()
    {
        var mgr = await MarketingUser("Mgr", CrmRole.Manager);
        var asha = await MarketingUser("Asha", CrmRole.Sales);
        var lead = new PlatformDemoRequest { Id = Guid.NewGuid(), FullName = "L", WorkEmail = "", Phone = "1", AcademyName = "L" };
        _db.Context.PlatformDemoRequests.Add(lead);
        _db.Context.CrmTasks.Add(new CrmTask { Id = Guid.NewGuid(), LeadId = lead.Id, Title = "Call", DueAtUtc = DateTime.UtcNow, AssignedToUserId = mgr.Id });
        await _db.Context.SaveChangesAsync();

        _me.UserId = asha.Id;
        await Assert.ThrowsAsync<ForbiddenException>(() => Crm().BulkAsync(new BulkCrmLeadsRequest { LeadIds = [lead.Id], OwnerUserIds = [asha.Id] }, default));

        _me.UserId = mgr.Id;
        await Crm().BulkAsync(new BulkCrmLeadsRequest { LeadIds = [lead.Id], OwnerUserIds = [asha.Id] }, default);
        _db.Context.ChangeTracker.Clear();
        Assert.Equal(asha.Id, (await _db.Context.PlatformDemoRequests.FirstAsync(x => x.Id == lead.Id)).AssignedToUserId);
        Assert.Equal(asha.Id, (await _db.Context.CrmTasks.FirstAsync(x => x.LeadId == lead.Id)).AssignedToUserId);
    }

    [Theory]
    [InlineData("2026-11-17T02:00:00Z", 0, "2026-11-17T04:30:00Z")]   // 07:30 IST: due at 10:00 IST today
    [InlineData("2026-11-17T10:00:00Z", 0, "2026-11-17T10:15:00Z")]   // 15:30 IST: 10:00 has passed, due in 15 min
    [InlineData("2026-11-17T10:00:00Z", 3, "2026-11-20T04:30:00Z")]   // three days later at 10:00 IST
    [InlineData("2026-11-17T20:00:00Z", 1, "2026-11-19T04:30:00Z")]   // 01:30 IST on the 18th: next day is the 19th
    public void PlanStepsFallDueAtTenInTheMorningIndiaTime(string start, int day, string expected) =>
        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), CrmRules.StepDueUtc(DateTime.Parse(start).ToUniversalTime(), day));

    [Fact]
    public async Task TasksCanOnlyGoToTeamMembers()
    {
        _me.UserId = (await MarketingUser("Mgr", CrmRole.Manager)).Id;
        await Assert.ThrowsAsync<DomainValidationException>(() => Crm().SaveTaskAsync(null, new SaveCrmTaskRequest { Title = "x", DueAtUtc = DateTime.UtcNow, AssignedToUserId = Guid.NewGuid() }, default));
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

public sealed class PlanPriceRuleTests
{
    [Fact]
    public void OnlyOnePriceOnSalePerPeriodAndCurrency()
    {
        var current = new PlanPrice { BillingInterval = BillingInterval.Month, Amount = 5999, IsActive = true };
        var retired = new PlanPrice { BillingInterval = BillingInterval.Month, Amount = 7999, IsActive = false };
        var yearly = new PlanPrice { BillingInterval = BillingInterval.Year, Amount = 59990, IsActive = true };
        var usd = new PlanPrice { BillingInterval = BillingInterval.Month, Currency = "USD", Amount = 79, IsActive = true };
        iucs.meettomanage.api.Controllers.PlatformSaasController.EnsureOneActivePricePerPeriod([current, retired, yearly, usd, current]); // duplicates are fine

        retired.IsActive = true; // someone re-ticks the old price while the current one is still on sale
        var ex = Assert.Throws<DomainValidationException>(() => iucs.meettomanage.api.Controllers.PlatformSaasController.EnsureOneActivePricePerPeriod([current, retired, yearly]));
        Assert.Contains("monthly INR", ex.Message);
    }
}
