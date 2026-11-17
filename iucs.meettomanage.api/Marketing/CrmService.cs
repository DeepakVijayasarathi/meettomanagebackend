using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Marketing;

/// <summary>
/// The marketing console's CRM: the team and their roles, tasks, adding and importing leads, logging calls and
/// meetings, deal values, per-owner reports and the revenue forecast, and the customer academies (read from the
/// master database in multi-tenant mode).
/// </summary>
public sealed partial class CrmService(MeetToManageDbContext db, CrmAccess access, IPasswordHasher hasher, IServiceProvider services)
{
    private IDbContextFactory<MasterDbContext>? Master => services.GetService<IDbContextFactory<MasterDbContext>>();
    private Guid? CurrentTenantId => services.GetService<TenantContext>()?.TenantId;

    private static string? NullIfEmpty(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    private static string Name(User u) => $"{u.FirstName} {u.LastName}".Trim();
    private static string? Clean(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(v.Trim().Length, max)];

    private async Task<Dictionary<Guid, string>> TeamNamesAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(x => x.Role == UserRole.Marketing).ToDictionaryAsync(x => x.Id, x => (x.FirstName + " " + x.LastName).Trim(), ct);

    private void Log(Guid leadId, string type, string detail) =>
        db.MarketingLeadActivities.Add(new MarketingLeadActivity { Id = Guid.NewGuid(), PlatformDemoRequestId = leadId, Type = type[..Math.Min(type.Length, 50)], Detail = detail[..Math.Min(detail.Length, 1000)] });

    // ================================================================== me and team

    public async Task<CrmMe> MeAsync(CancellationToken ct)
    {
        var role = await access.RoleAsync(ct);
        var user = access.UserId is { } id ? await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) : null;
        return new CrmMe(access.UserId, user is null ? "" : Name(user), role);
    }

    public async Task<List<CrmMember>> TeamAsync(CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Where(x => x.Role == UserRole.Marketing).OrderBy(x => x.FirstName).ToListAsync(ct);
        var roles = await db.CrmTeamMembers.AsNoTracking().ToDictionaryAsync(x => x.UserId, x => x.Role, ct);
        var openLeads = await db.PlatformDemoRequests.AsNoTracking().Where(x => x.ArchivedAtUtc == null && x.AssignedToUserId != null && x.Stage != LeadStage.Customer && x.Stage != LeadStage.Lost)
            .GroupBy(x => x.AssignedToUserId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var openTasks = await db.CrmTasks.AsNoTracking().Where(x => x.CompletedAtUtc == null && x.AssignedToUserId != null)
            .GroupBy(x => x.AssignedToUserId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return users.Select(u => new CrmMember(u.Id, Name(u), u.Email, u.Status == UserStatus.Active, roles.TryGetValue(u.Id, out var r) ? r : CrmRole.Manager,
            openLeads.GetValueOrDefault(u.Id), openTasks.GetValueOrDefault(u.Id))).ToList();
    }

    public async Task<CrmMember> AddMemberAsync(AddCrmMemberRequest request, CancellationToken ct)
    {
        await access.RequireManagerAsync(ct);
        var email = request.Email.Trim().ToLowerInvariant();
        if (!PlatformDemoRequestService.LooksLikeEmail(email)) throw new DomainValidationException("Enter a valid e-mail address.");
        if (!PinPattern().IsMatch(request.Pin ?? "")) throw new DomainValidationException("The PIN must be exactly 4 digits.");
        if (await db.Users.AnyAsync(x => x.Email == email, ct)) throw new ConflictException("Someone with this e-mail already has an account here.");
        var user = new User
        {
            Id = Guid.NewGuid(), Email = email, PinHash = hasher.Hash(request.Pin!),
            FirstName = request.FirstName.Trim(), LastName = request.LastName?.Trim() ?? "",
            Phone = Clean(request.Phone, 20), Role = UserRole.Marketing, TimeZoneId = "Asia/Kolkata",
        };
        db.Users.Add(user);
        db.CrmTeamMembers.Add(new CrmTeamMember { Id = Guid.NewGuid(), UserId = user.Id, Role = request.Role });
        await db.SaveChangesAsync(ct);
        return new CrmMember(user.Id, Name(user), user.Email, true, request.Role, 0, 0);
    }

    public async Task UpdateMemberAsync(Guid userId, UpdateCrmMemberRequest request, CancellationToken ct)
    {
        await access.RequireManagerAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId && x.Role == UserRole.Marketing, ct) ?? throw new NotFoundException("Team member", userId);
        var team = await TeamAsync(ct);
        var activeManagers = team.Count(m => m.Active && m.Role == CrmRole.Manager);
        var isActiveManager = team.Any(m => m.UserId == userId && m.Active && m.Role == CrmRole.Manager);
        var losesManager = (request.Role is { } r && r != CrmRole.Manager) || request.Active == false;
        if (isActiveManager && losesManager && activeManagers <= 1)
            throw new DomainValidationException("Keep at least one active manager on the team.");

        if (request.Role is { } role)
        {
            var row = await db.CrmTeamMembers.FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (row is null) db.CrmTeamMembers.Add(new CrmTeamMember { Id = Guid.NewGuid(), UserId = userId, Role = role });
            else row.Role = role;
        }
        if (request.Active is { } active) user.Status = active ? UserStatus.Active : UserStatus.Inactive;
        if (request.Pin is { Length: > 0 } pin)
        {
            if (!PinPattern().IsMatch(pin)) throw new DomainValidationException("The PIN must be exactly 4 digits.");
            user.PinHash = hasher.Hash(pin);
        }
        if (request.ReassignTo is { } to)
        {
            if (to == userId) throw new DomainValidationException("Hand the work to someone else.");
            await access.EnsureCanOwnAsync(to, ct);
            var newName = Name(await db.Users.AsNoTracking().FirstAsync(x => x.Id == to, ct));
            var leads = await db.PlatformDemoRequests.Where(x => x.AssignedToUserId == userId && x.Stage != LeadStage.Customer && x.Stage != LeadStage.Lost).ToListAsync(ct);
            foreach (var lead in leads) { lead.AssignedToUserId = to; lead.AssignedTo = newName; Log(lead.Id, "Updated", $"Reassigned to {newName}."); }
            await db.CrmTasks.Where(x => x.AssignedToUserId == userId && x.CompletedAtUtc == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.AssignedToUserId, to), ct);
        }
        await db.SaveChangesAsync(ct);
    }

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex PinPattern();

    // ================================================================== tasks

    public async Task<List<CrmTaskDto>> TasksAsync(string scope, string status, Guid? leadId, Guid? tenantId, CancellationToken ct)
    {
        var role = await access.RoleAsync(ct);
        var me = access.UserId;
        var q = db.CrmTasks.AsNoTracking().AsQueryable();
        if (leadId is { } l) { await access.EnsureCanSeeLeadAsync(l, ct); q = q.Where(x => x.LeadId == l); }
        else if (tenantId is { } t) q = q.Where(x => x.TenantId == t);
        else if (scope == "mine" || role == CrmRole.Sales) q = q.Where(x => x.AssignedToUserId == me || (role == CrmRole.Sales && x.AssignedToUserId == null));
        q = status switch
        {
            "done" => q.Where(x => x.CompletedAtUtc != null).OrderByDescending(x => x.CompletedAtUtc),
            "all" => q.OrderBy(x => x.CompletedAtUtc != null).ThenBy(x => x.DueAtUtc),
            _ => q.Where(x => x.CompletedAtUtc == null).OrderBy(x => x.DueAtUtc),
        };
        var tasks = await q.Take(300).ToListAsync(ct);
        return await ToDtosAsync(tasks, ct);
    }

    private async Task<List<CrmTaskDto>> ToDtosAsync(List<CrmTask> tasks, CancellationToken ct)
    {
        var names = await TeamNamesAsync(ct);
        var leadIds = tasks.Where(x => x.LeadId != null).Select(x => x.LeadId!.Value).Distinct().ToList();
        var leads = await db.PlatformDemoRequests.AsNoTracking().Where(x => leadIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => new { x.FullName, x.AcademyName, x.Phone, x.WorkEmail }, ct);
        var tenantIds = tasks.Where(x => x.TenantId != null).Select(x => x.TenantId!.Value).Distinct().ToList();
        var tenants = new Dictionary<Guid, string>();
        if (tenantIds.Count > 0 && Master is { } factory)
        {
            await using var master = await factory.CreateDbContextAsync(ct);
            tenants = await master.Tenants.AsNoTracking().Where(x => tenantIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        }
        return tasks.Select(t => new CrmTaskDto(t.Id, t.Title, t.Type, t.DueAtUtc, t.AssignedToUserId,
            t.AssignedToUserId is { } a ? names.GetValueOrDefault(a) : null, t.LeadId,
            t.LeadId is { } l && leads.TryGetValue(l, out var lead) ? $"{lead.AcademyName} ({lead.FullName})" : null,
            t.TenantId, t.TenantId is { } tid ? tenants.GetValueOrDefault(tid) : null, t.Notes, t.CompletedAtUtc, t.Outcome,
            t.LeadId is { } pl && leads.TryGetValue(pl, out var contact) ? NullIfEmpty(contact.Phone) : null,
            t.LeadId is { } el && leads.TryGetValue(el, out var mail) ? NullIfEmpty(mail.WorkEmail) : null, t.PlanKey)).ToList();
    }

    public async Task<CrmTaskDto> SaveTaskAsync(Guid? id, SaveCrmTaskRequest request, CancellationToken ct)
    {
        await access.RequireWriteAsync(ct);
        var role = await access.RoleAsync(ct);
        if (request.LeadId is { } leadId)
        {
            await access.EnsureCanSeeLeadAsync(leadId, ct);
            if (!await db.PlatformDemoRequests.AnyAsync(x => x.Id == leadId, ct)) throw new NotFoundException("Lead", leadId);
        }
        // An explicit owner must be able to own work; otherwise the lead's owner, unless they've left, then me.
        var assignee = request.AssignedToUserId;
        if (assignee is { } chosen) await access.EnsureCanOwnAsync(chosen, ct);
        else
        {
            var leadOwner = request.LeadId is { } lid ? await db.PlatformDemoRequests.Where(x => x.Id == lid).Select(x => x.AssignedToUserId).FirstOrDefaultAsync(ct) : null;
            assignee = leadOwner is { } lo && await access.CanOwnAsync(lo, ct) ? lo : access.UserId;
        }
        if (role == CrmRole.Sales && assignee != access.UserId) throw new ForbiddenException("Sales reps can only give tasks to themselves.");

        CrmTask task;
        if (id is { } existing)
        {
            task = await db.CrmTasks.FirstOrDefaultAsync(x => x.Id == existing, ct) ?? throw new NotFoundException("Task", existing);
            if (role == CrmRole.Sales && task.AssignedToUserId is { } owner && owner != access.UserId) throw new ForbiddenException("This task belongs to someone else.");
            if (task.LeadId is { } taskLead) await access.EnsureCanSeeLeadAsync(taskLead, ct);
        }
        else
        {
            task = new CrmTask { Id = Guid.NewGuid(), LeadId = request.LeadId, TenantId = request.TenantId };
            db.CrmTasks.Add(task);
            if (request.LeadId is { } l) Log(l, "Task", $"Task added: {request.Title.Trim()} (due {request.DueAtUtc:dd MMM})");
        }
        task.Title = request.Title.Trim();
        task.Type = request.Type;
        task.DueAtUtc = DateTime.SpecifyKind(request.DueAtUtc, DateTimeKind.Utc);
        task.AssignedToUserId = assignee;
        task.Notes = Clean(request.Notes, 1000);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([task], ct))[0];
    }

    public async Task<CrmTaskDto> CompleteTaskAsync(Guid id, CompleteCrmTaskRequest request, CancellationToken ct)
    {
        await access.RequireWriteAsync(ct);
        var task = await db.CrmTasks.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Task", id);
        if (await access.RoleAsync(ct) == CrmRole.Sales && task.AssignedToUserId is { } owner && owner != access.UserId)
            throw new ForbiddenException("This task belongs to someone else.");
        if (request.Reopen)
        {
            task.CompletedAtUtc = null;
        }
        else
        {
            task.CompletedAtUtc = DateTime.UtcNow;
            task.Outcome = Clean(request.Outcome, 1000);
            if (task.LeadId is { } leadId) Log(leadId, "Task", $"Done: {task.Title}{(task.Outcome is null ? "" : $" — {task.Outcome}")}");
        }
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([task], ct))[0];
    }

    public async Task DeleteTaskAsync(Guid id, CancellationToken ct)
    {
        await access.RequireWriteAsync(ct);
        var task = await db.CrmTasks.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Task", id);
        if (await access.RoleAsync(ct) == CrmRole.Sales && task.AssignedToUserId is { } owner && owner != access.UserId)
            throw new ForbiddenException("This task belongs to someone else.");
        db.CrmTasks.Remove(task);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The "My day" page: my open tasks by urgency, today's demos and leads waiting for a first reply.</summary>
    public async Task<CrmToday> TodayAsync(int utcOffsetMinutes, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var localMidnight = now.AddMinutes(utcOffsetMinutes).Date;
        var endOfToday = localMidnight.AddDays(1).AddMinutes(-utcOffsetMinutes);
        var startOfToday = localMidnight.AddMinutes(-utcOffsetMinutes);
        var tasks = await TasksAsync("mine", "open", null, null, ct);
        var visible = await access.VisibleToAsync(ct);
        var me = access.UserId;
        var leads = db.PlatformDemoRequests.AsNoTracking().Where(x => x.ArchivedAtUtc == null);
        if (visible is { } v) leads = leads.Where(x => x.AssignedToUserId == null || x.AssignedToUserId == v);
        var demos = await leads.Where(x => x.Stage == LeadStage.DemoBooked && x.DemoAtUtc >= startOfToday && x.DemoAtUtc < endOfToday)
            .OrderBy(x => x.DemoAtUtc).Select(x => new CrmLeadBrief(x.Id, x.FullName, x.AcademyName, x.AssignedTo, x.DemoAtUtc, x.Stage)).ToListAsync(ct);
        var waiting = await leads.Where(x => x.Stage == LeadStage.New && (x.AssignedToUserId == me || x.AssignedToUserId == null))
            .OrderBy(x => x.CreatedAtUtc).Take(20).Select(x => new CrmLeadBrief(x.Id, x.FullName, x.AcademyName, x.AssignedTo, x.CreatedAtUtc, x.Stage)).ToListAsync(ct);
        var followUps = await leads.Where(x => x.AssignedToUserId == me && x.NextFollowUpAtUtc < endOfToday && x.Stage != LeadStage.Customer && x.Stage != LeadStage.Lost)
            .OrderBy(x => x.NextFollowUpAtUtc).Take(30).Select(x => new CrmLeadBrief(x.Id, x.FullName, x.AcademyName, x.AssignedTo, x.NextFollowUpAtUtc, x.Stage)).ToListAsync(ct);
        return new CrmToday(
            tasks.Where(t => t.DueAtUtc < startOfToday).ToList(),
            tasks.Where(t => t.DueAtUtc >= startOfToday && t.DueAtUtc < endOfToday).ToList(),
            tasks.Where(t => t.DueAtUtc >= endOfToday && t.DueAtUtc < endOfToday.AddDays(7)).ToList(),
            demos, waiting, followUps);
    }

    // ================================================================== leads

    public async Task<PlatformDemoRequest> CreateLeadAsync(CreateCrmLeadRequest request, CancellationToken ct)
    {
        await access.RequireWriteAsync(ct);
        var result = await ImportAsync(new ImportCrmLeadsRequest { Rows = [request.ToRow()], AssignedToUserId = request.AssignedToUserId, Source = request.Source ?? "Manual" }, ct, single: true);
        if (result.Skipped.FirstOrDefault() is { } skip) throw new DomainValidationException(skip.Reason);
        return await db.PlatformDemoRequests.AsNoTracking().FirstAsync(x => x.Id == result.CreatedIds[0], ct);
    }

    public Task<CrmImportResult> ImportLeadsAsync(ImportCrmLeadsRequest request, CancellationToken ct) => ImportAsync(request, ct, single: false);

    /// <summary>
    /// Adds leads from a list. Rows need a name or academy and an e-mail or phone; a row matching an existing lead
    /// (same e-mail, or same last ten phone digits) or an earlier row is skipped and reported. No e-mail is sent:
    /// imported people did not ask to be contacted, so the acknowledgement automation does not apply.
    /// </summary>
    private async Task<CrmImportResult> ImportAsync(ImportCrmLeadsRequest request, CancellationToken ct, bool single)
    {
        await access.RequireWriteAsync(ct);
        if (request.Rows.Count == 0) throw new DomainValidationException("There are no rows to import.");
        if (request.Rows.Count > 2000) throw new DomainValidationException("Import up to 2,000 rows at a time.");
        var role = await access.RoleAsync(ct);
        // Owners: a sales rep's imports are theirs; a manager picks one owner, several (split evenly in turn) or none.
        var ownerIds = role == CrmRole.Sales ? [access.UserId!.Value]
            : request.OwnerUserIds.Count > 0 ? request.OwnerUserIds.Distinct().ToList()
            : request.AssignedToUserId is { } one ? [one] : new List<Guid>();
        var owners = new List<(Guid Id, string Name)>();
        foreach (var oid in ownerIds)
        {
            await access.EnsureCanOwnAsync(oid, ct);
            owners.Add((oid, Name(await db.Users.AsNoTracking().FirstAsync(x => x.Id == oid, ct))));
        }
        var plan = request.FollowUpPlan is { Length: > 0 } key ? CrmRules.Plan(key) ?? throw new DomainValidationException("Unknown follow-up plan.") : null;

        var existing = await db.PlatformDemoRequests.AsNoTracking().Select(x => new { x.WorkEmail, x.Phone }).ToListAsync(ct);
        var emails = existing.Where(x => !string.IsNullOrWhiteSpace(x.WorkEmail)).Select(x => x.WorkEmail.ToLowerInvariant()).ToHashSet();
        var phones = existing.Select(x => CrmRules.PhoneKey(x.Phone)).Where(x => x.Length >= 7).ToHashSet();
        var source = Clean(request.Source, 100) ?? "Import";
        var created = new List<Guid>();
        var skipped = new List<CrmImportSkip>();
        var now = DateTime.UtcNow;

        for (var i = 0; i < request.Rows.Count; i++)
        {
            var row = request.Rows[i];
            var email = Clean(row.Email, 256)?.ToLowerInvariant() ?? "";
            var phone = Clean(row.Phone, 20) ?? "";
            var academy = Clean(row.AcademyName, 150);
            var name = Clean(row.FullName, 150) ?? (academy is null ? null : $"{academy} team");
            if (name is null) { skipped.Add(new(i, "Needs a name or an academy.")); continue; }
            if (email.Length == 0 && CrmRules.PhoneKey(phone).Length < 7) { skipped.Add(new(i, "Needs an e-mail or a phone number.")); continue; }
            if (email.Length > 0 && !PlatformDemoRequestService.LooksLikeEmail(email)) { skipped.Add(new(i, $"\"{email}\" isn't a valid e-mail.")); continue; }
            var phoneKey = CrmRules.PhoneKey(phone);
            if ((email.Length > 0 && emails.Contains(email)) || (phoneKey.Length >= 7 && phones.Contains(phoneKey)))
            { skipped.Add(new(i, single ? "A lead with this e-mail or phone already exists. Search for it in Leads." : "Already a lead (same e-mail or phone).")); continue; }
            if (email.Length > 0) emails.Add(email);
            if (phoneKey.Length >= 7) phones.Add(phoneKey);

            var website = Clean(row.Website, 300);
            var notes = Clean(row.Notes, 1000);
            var lead = new PlatformDemoRequest
            {
                Id = Guid.NewGuid(), FullName = name, WorkEmail = email, Phone = phone, AcademyName = academy ?? "Not provided",
                Source = Clean(row.Source, 100) ?? source, Region = Clean(row.Region, 100), City = Clean(row.City, 100),
                Segment = Clean(row.Segment, 100), Website = website, StudentCount = row.StudentCount is > 0 and < 1_000_000 ? row.StudentCount : null,
                InternalNotes = notes, Stage = LeadStage.New, StageChangedAtUtc = now,
            };
            if (owners.Count > 0)
            {
                var (ownerId, ownerName) = owners[created.Count % owners.Count];
                lead.AssignedToUserId = ownerId;
                lead.AssignedTo = ownerName;
            }
            db.PlatformDemoRequests.Add(lead);
            Log(lead.Id, "Created", single ? $"Added by hand{(lead.AssignedTo is null ? "" : $", owner {lead.AssignedTo}")}." : $"Imported from a list ({source}){(lead.AssignedTo is null ? "" : $", owner {lead.AssignedTo}")}.");
            created.Add(lead.Id);
        }
        await db.SaveChangesAsync(ct);
        if (plan is not null && created.Count > 0) await StartPlanAsync(created, plan.Key, ct);
        return new CrmImportResult(created.Count, created, skipped);
    }

    public async Task<PlatformDemoRequest> UpdateDealAsync(Guid id, UpdateCrmDealRequest request, CancellationToken ct)
    {
        await access.RequireWriteAsync(ct);
        await access.EnsureCanSeeLeadAsync(id, ct);
        var lead = await db.PlatformDemoRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Lead", id);
        lead.ExpectedPlan = Clean(request.ExpectedPlan, 50);
        lead.ExpectedMonthlyValue = request.ExpectedMonthlyValue is >= 0 ? request.ExpectedMonthlyValue : null;
        lead.ExpectedCloseAtUtc = request.ExpectedCloseAtUtc is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null;
        lead.StudentCount = request.StudentCount is > 0 ? request.StudentCount : null;
        lead.Region = Clean(request.Region, 100);
        lead.City = Clean(request.City, 100);
        lead.Segment = Clean(request.Segment, 100);
        lead.Website = Clean(request.Website, 300);
        Log(id, "Updated", lead.ExpectedMonthlyValue is { } v ? $"Deal updated: ₹{v:N0}/month{(lead.ExpectedPlan is null ? "" : $" on {lead.ExpectedPlan}")}." : "Deal details updated.");
        await db.SaveChangesAsync(ct);
        return lead;
    }

    /// <summary>Records a call, meeting, e-mail or WhatsApp with its outcome; optionally schedules the next step.</summary>
    public async Task<PlatformDemoRequest> LogInteractionAsync(Guid id, LogCrmInteractionRequest request, CancellationToken ct)
    {
        await access.RequireWriteAsync(ct);
        await access.EnsureCanSeeLeadAsync(id, ct);
        var lead = await db.PlatformDemoRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Lead", id);
        var now = DateTime.UtcNow;
        var detail = Clean(request.Detail, 800);
        Log(id, request.Type, $"{request.Outcome}{(detail is null ? "" : $": {detail}")}");
        lead.LastContactedAtUtc = now;
        lead.FirstContactedAtUtc ??= now;
        if (lead.Stage == LeadStage.New) LeadLifecycle.Apply(lead, LeadStage.Contacted, now);
        if (Clean(request.NextStepTitle, 200) is { } next && request.NextStepDueAtUtc is { } due)
        {
            db.CrmTasks.Add(new CrmTask
            {
                Id = Guid.NewGuid(), LeadId = id, Title = next, Type = CrmTaskType.FollowUp,
                DueAtUtc = DateTime.SpecifyKind(due, DateTimeKind.Utc), AssignedToUserId = lead.AssignedToUserId is { } leadOwner && await access.CanOwnAsync(leadOwner, ct) ? leadOwner : access.UserId,
            });
            lead.NextFollowUpAtUtc = DateTime.SpecifyKind(due, DateTimeKind.Utc);
            Log(id, "Task", $"Next step: {next} (due {due:dd MMM})");
        }
        await db.SaveChangesAsync(ct);
        return lead;
    }

    // ================================================================== follow-up plans and bulk work

    /// <summary>
    /// Creates the plan's tasks on each lead, owned by the lead's owner (or whoever starts it if the lead has no
    /// owner who can work it). Skips closed leads and leads already running the same plan.
    /// </summary>
    public async Task<CrmBulkResult> StartPlanAsync(IReadOnlyCollection<Guid> leadIds, string planKey, CancellationToken ct)
    {
        await access.RequireWriteAsync(ct);
        var plan = CrmRules.Plan(planKey) ?? throw new DomainValidationException("Unknown follow-up plan.");
        var visible = await access.VisibleToAsync(ct);
        var leads = await db.PlatformDemoRequests.Where(x => leadIds.Contains(x.Id)).ToListAsync(ct);
        var running = (await db.CrmTasks.AsNoTracking().Where(x => x.LeadId != null && leadIds.Contains(x.LeadId!.Value) && x.PlanKey == plan.Key && x.CompletedAtUtc == null)
            .Select(x => x.LeadId!.Value).ToListAsync(ct)).ToHashSet();
        var now = DateTime.UtcNow;
        var started = 0;
        var skipped = new List<string>();
        foreach (var lead in leads)
        {
            if (!CrmAccess.CanSee(visible, lead.AssignedToUserId)) { skipped.Add($"{lead.AcademyName}: belongs to someone else"); continue; }
            if (plan.StopAt.Contains(lead.Stage)) { skipped.Add($"{lead.AcademyName}: already {LeadLifecycle.Label(lead.Stage).ToLowerInvariant()}"); continue; }
            if (running.Contains(lead.Id)) { skipped.Add($"{lead.AcademyName}: plan already running"); continue; }
            var owner = lead.AssignedToUserId is { } o && await access.CanOwnAsync(o, ct) ? o : access.UserId;
            foreach (var step in plan.Steps)
                db.CrmTasks.Add(new CrmTask
                {
                    Id = Guid.NewGuid(), LeadId = lead.Id, Title = step.Title, Type = step.Type,
                    DueAtUtc = CrmRules.StepDueUtc(now, step.Day), AssignedToUserId = owner, PlanKey = plan.Key,
                });
            var first = CrmRules.StepDueUtc(now, plan.Steps[0].Day);
            if (lead.NextFollowUpAtUtc is null || lead.NextFollowUpAtUtc < now || lead.NextFollowUpAtUtc > first) lead.NextFollowUpAtUtc = first;
            Log(lead.Id, "Task", $"Follow-up plan started: {plan.Name} ({plan.Steps.Length} steps).");
            started++;
        }
        await db.SaveChangesAsync(ct);
        return new CrmBulkResult(started, skipped);
    }

    /// <summary>Assigns selected leads (split evenly between the chosen people) and/or starts a plan on them.</summary>
    public async Task<CrmBulkResult> BulkAsync(BulkCrmLeadsRequest request, CancellationToken ct)
    {
        await access.RequireWriteAsync(ct);
        if (request.LeadIds.Count == 0) throw new DomainValidationException("Select some leads first.");
        var done = 0;
        var skipped = new List<string>();
        if (request.OwnerUserIds.Count > 0)
        {
            await access.RequireManagerAsync(ct);
            var owners = new List<(Guid Id, string Name)>();
            foreach (var oid in request.OwnerUserIds.Distinct())
            {
                await access.EnsureCanOwnAsync(oid, ct);
                owners.Add((oid, Name(await db.Users.AsNoTracking().FirstAsync(x => x.Id == oid, ct))));
            }
            var leads = await db.PlatformDemoRequests.Where(x => request.LeadIds.Contains(x.Id)).OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
            for (var i = 0; i < leads.Count; i++)
            {
                var (id, name) = owners[i % owners.Count];
                if (leads[i].AssignedToUserId == id) continue;
                leads[i].AssignedToUserId = id;
                leads[i].AssignedTo = name;
                Log(leads[i].Id, "Updated", $"Assigned to {name}.");
                // Open tasks follow the lead to its new owner.
                await db.CrmTasks.Where(x => x.LeadId == leads[i].Id && x.CompletedAtUtc == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.AssignedToUserId, id), ct);
            }
            await db.SaveChangesAsync(ct);
            done = leads.Count;
        }
        if (request.FollowUpPlan is { Length: > 0 } plan)
        {
            var result = await StartPlanAsync(request.LeadIds, plan, ct);
            done = Math.Max(done, result.Done);
            skipped.AddRange(result.Skipped);
        }
        return new CrmBulkResult(done, skipped);
    }

    // ================================================================== reports

    public async Task<CrmReport> ReportAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 365);
        var now = DateTime.UtcNow;
        var since = now.AddDays(-days);
        var visible = await access.VisibleToAsync(ct);
        var names = await TeamNamesAsync(ct);
        var all = db.PlatformDemoRequests.AsNoTracking().Where(x => x.ArchivedAtUtc == null);
        if (visible is { } v) all = all.Where(x => x.AssignedToUserId == v);

        var period = await all.Where(x => x.CreatedAtUtc >= since).Select(x => new { x.AssignedToUserId, x.Stage, x.CreatedAtUtc, x.FirstContactedAtUtc, x.DemoAtUtc }).ToListAsync(ct);
        var open = await all.Where(x => x.Stage != LeadStage.Customer && x.Stage != LeadStage.Lost)
            .Select(x => new { x.AssignedToUserId, x.Stage, x.ExpectedMonthlyValue, x.ExpectedCloseAtUtc }).ToListAsync(ct);
        var won = await all.Where(x => x.Stage == LeadStage.Customer && x.StageChangedAtUtc >= since).Select(x => new { x.AssignedToUserId, x.ExpectedMonthlyValue }).ToListAsync(ct);
        var tasks = await db.CrmTasks.AsNoTracking().Where(x => x.AssignedToUserId != null && (x.CompletedAtUtc == null || x.CompletedAtUtc >= since))
            .Select(x => new { x.AssignedToUserId, x.CompletedAtUtc, x.DueAtUtc }).ToListAsync(ct);

        static int Rank(LeadStage s) => s switch { LeadStage.New => 0, LeadStage.Contacted => 1, LeadStage.DemoBooked => 2, LeadStage.DemoDone => 3, LeadStage.Trial => 4, LeadStage.ActiveTrial => 5, LeadStage.Customer => 6, _ => -1 };
        var owners = period.Select(x => x.AssignedToUserId).Concat(open.Select(x => x.AssignedToUserId)).Concat(won.Select(x => x.AssignedToUserId)).Distinct().ToList();
        if (visible is { } mine && !owners.Contains(mine)) owners.Add(mine);
        var rows = owners.Select(o =>
        {
            var leads = period.Where(x => x.AssignedToUserId == o).ToList();
            var responses = leads.Where(x => x.FirstContactedAtUtc is not null).Select(x => (x.FirstContactedAtUtc!.Value - x.CreatedAtUtc).TotalHours).Where(h => h >= 0).OrderBy(h => h).ToList();
            double? median = responses.Count == 0 ? null : responses.Count % 2 == 1 ? responses[responses.Count / 2] : (responses[responses.Count / 2 - 1] + responses[responses.Count / 2]) / 2;
            return new CrmOwnerRow(o, o is { } id ? names.GetValueOrDefault(id, "Former member") : "Unassigned",
                leads.Count, leads.Count(x => x.FirstContactedAtUtc != null), median is null ? null : Math.Round(median.Value, 1),
                leads.Count(x => x.DemoAtUtc != null || Rank(x.Stage) >= 2), leads.Count(x => Rank(x.Stage) >= 4),
                won.Count(x => x.AssignedToUserId == o), leads.Count(x => x.Stage == LeadStage.Lost),
                won.Where(x => x.AssignedToUserId == o).Sum(x => x.ExpectedMonthlyValue ?? 0),
                open.Count(x => x.AssignedToUserId == o), open.Where(x => x.AssignedToUserId == o).Sum(x => x.ExpectedMonthlyValue ?? 0),
                tasks.Count(x => x.AssignedToUserId == o && x.CompletedAtUtc == null && x.DueAtUtc < now),
                tasks.Count(x => x.AssignedToUserId == o && x.CompletedAtUtc != null));
        }).OrderBy(r => r.OwnerId is null).ThenByDescending(r => r.Won).ThenByDescending(r => r.Leads).ToList();

        var byStage = open.GroupBy(x => x.Stage).OrderBy(g => Rank(g.Key)).Select(g =>
        {
            var value = g.Sum(x => x.ExpectedMonthlyValue ?? 0);
            var p = CrmRules.WinProbability(g.Key);
            return new CrmForecastStage(g.Key, LeadLifecycle.Label(g.Key), g.Count(), value, p, Math.Round(value * (decimal)p, 0));
        }).ToList();
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var byMonth = Enumerable.Range(0, 6).Select(i =>
        {
            var start = monthStart.AddMonths(i);
            var inMonth = open.Where(x => x.ExpectedCloseAtUtc is { } c && (i == 0 ? c < start.AddMonths(1) : c >= start && c < start.AddMonths(1))).ToList();
            return new CrmForecastMonth(start.ToString("yyyy-MM"), inMonth.Count, Math.Round(inMonth.Sum(x => (x.ExpectedMonthlyValue ?? 0) * (decimal)CrmRules.WinProbability(x.Stage)), 0));
        }).ToList();

        return new CrmReport(days, rows, byStage, byStage.Sum(x => x.Weighted), open.Count(x => x.ExpectedMonthlyValue is null),
            won.Sum(x => x.ExpectedMonthlyValue ?? 0), byMonth);
    }

    // ================================================================== customers

    public bool CustomersAvailable => Master is not null;

    public async Task<List<CrmCustomerRow>> CustomersAsync(string? search, CustomerHealth? health, CancellationToken ct)
    {
        if (Master is not { } factory) return [];
        var now = DateTime.UtcNow;
        await using var master = await factory.CreateDbContextAsync(ct);
        var self = CurrentTenantId;
        var tenants = await master.Tenants.AsNoTracking().Where(x => x.Id != self && x.Status != domain.Entities.Tenancy.TenantStatus.Failed).ToListAsync(ct);
        var ids = tenants.Select(x => x.Id).ToList();
        var subs = await master.TenantSubscriptions.AsNoTracking().Include(x => x.Plan).Include(x => x.PlanPrice).Where(x => ids.Contains(x.TenantId)).ToDictionaryAsync(x => x.TenantId, ct);
        var recent = DateOnly.FromDateTime(now.AddDays(-7));
        var usage = (await master.TenantUsageSnapshots.AsNoTracking().Where(x => ids.Contains(x.TenantId) && x.SnapshotDate >= recent).ToListAsync(ct))
            .GroupBy(x => x.TenantId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.SnapshotDate).First());
        var profiles = await master.TenantBillingProfiles.AsNoTracking().Where(x => ids.Contains(x.TenantId)).ToDictionaryAsync(x => x.TenantId, ct);
        var owners = await db.PlatformDemoRequests.AsNoTracking().Where(x => x.TenantId != null && ids.Contains(x.TenantId!.Value))
            .Select(x => new { x.TenantId, x.AssignedTo }).ToListAsync(ct);
        var openTasks = await db.CrmTasks.AsNoTracking().Where(x => x.TenantId != null && x.CompletedAtUtc == null)
            .GroupBy(x => x.TenantId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var term = search?.Trim().ToLowerInvariant();
        return tenants.Select(t =>
        {
            var sub = subs.GetValueOrDefault(t.Id);
            var use = usage.GetValueOrDefault(t.Id);
            var (h, reasons) = CrmRules.Health(t, sub, use, now);
            var monthly = sub?.PlanPrice is { Amount: > 0 } price ? (price.BillingInterval == BillingInterval.Year ? Math.Round(price.Amount / 12, 0, MidpointRounding.AwayFromZero) : price.Amount) : 0;
            return new CrmCustomerRow(t.Id, t.Name, t.Code, sub?.Plan?.Name, sub?.Status.ToString(), monthly, use?.ActiveStudents, use?.SessionsMonth,
                sub?.TrialEndsAtUtc, sub?.CurrentPeriodEndUtc, h, reasons, profiles.GetValueOrDefault(t.Id)?.BillingEmail,
                owners.FirstOrDefault(o => o.TenantId == t.Id && o.AssignedTo != null)?.AssignedTo, openTasks.GetValueOrDefault(t.Id), t.CreatedAtUtc);
        })
        .Where(r => health is null || r.Health == health)
        .Where(r => term is null || r.Name.ToLowerInvariant().Contains(term) || r.Code.Contains(term) || (r.Email ?? "").Contains(term))
        .OrderBy(r => r.Health switch { CustomerHealth.AtRisk => 0, CustomerHealth.Trial => 1, CustomerHealth.New => 2, CustomerHealth.Healthy => 3, _ => 4 })
        .ThenByDescending(r => r.MonthlyValue).ThenBy(r => r.Name).ToList();
    }

    public async Task<CrmCustomerDetail> CustomerAsync(Guid tenantId, CancellationToken ct)
    {
        if (Master is not { } factory) throw new DomainValidationException("Customers are only available in multi-tenant mode.");
        var row = (await CustomersAsync(null, null, ct)).FirstOrDefault(x => x.TenantId == tenantId) ?? throw new NotFoundException("Customer", tenantId);
        await using var master = await factory.CreateDbContextAsync(ct);
        var invoices = await master.PlatformInvoices.AsNoTracking().Where(x => x.TenantId == tenantId).OrderByDescending(x => x.IssuedAtUtc).Take(12)
            .Select(x => new CrmInvoice(x.InvoiceNumber, x.Total, x.Currency, x.Status.ToString(), x.IssuedAtUtc, x.DueAtUtc, x.PaidAtUtc)).ToListAsync(ct);
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-45));
        var history = await master.TenantUsageSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotDate >= since).OrderBy(x => x.SnapshotDate)
            .Select(x => new CrmUsagePoint(x.SnapshotDate, x.ActiveStudents, x.StaffCount, x.SessionsMonth)).ToListAsync(ct);
        var profile = await master.TenantBillingProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var leads = await db.PlatformDemoRequests.AsNoTracking().Where(x => x.TenantId == tenantId)
            .Select(x => new CrmLeadBrief(x.Id, x.FullName, x.AcademyName, x.AssignedTo, x.CreatedAtUtc, x.Stage)).ToListAsync(ct);
        var notes = await db.CrmCustomerNotes.AsNoTracking().Where(x => x.TenantId == tenantId).OrderByDescending(x => x.CreatedAtUtc).Take(50)
            .Select(x => new CrmNote(x.Id, x.Text, x.Author, x.CreatedAtUtc)).ToListAsync(ct);
        var tasks = await TasksAsync("all", "all", null, tenantId, ct);
        return new CrmCustomerDetail(row, profile?.BillingPhone, profile?.LegalName, invoices, history, leads, notes, tasks);
    }

    public async Task<CrmNote> AddCustomerNoteAsync(Guid tenantId, string text, CancellationToken ct)
    {
        await access.RequireWriteAsync(ct);
        var clean = Clean(text, 2000) ?? throw new DomainValidationException("Write the note first.");
        var me = await MeAsync(ct);
        var note = new CrmCustomerNote { Id = Guid.NewGuid(), TenantId = tenantId, Text = clean, Author = me.Name };
        db.CrmCustomerNotes.Add(note);
        await db.SaveChangesAsync(ct);
        return new CrmNote(note.Id, note.Text, note.Author, note.CreatedAtUtc == default ? DateTime.UtcNow : note.CreatedAtUtc);
    }
}

// ---------------------------------------------------------------------- contracts

public sealed record CrmMe(Guid? UserId, string Name, CrmRole Role);
public sealed record CrmMember(Guid UserId, string Name, string Email, bool Active, CrmRole Role, int OpenLeads, int OpenTasks);

public sealed class AddCrmMemberRequest
{
    [Required, MaxLength(100)] public string FirstName { get; set; } = "";
    [MaxLength(100)] public string? LastName { get; set; }
    [Required, MaxLength(256)] public string Email { get; set; } = "";
    [MaxLength(20)] public string? Phone { get; set; }
    [Required] public string? Pin { get; set; }
    public CrmRole Role { get; set; } = CrmRole.Sales;
}

public sealed class UpdateCrmMemberRequest
{
    public CrmRole? Role { get; set; }
    public bool? Active { get; set; }
    /// <summary>New 4-digit sign-in PIN, when the manager resets it.</summary>
    public string? Pin { get; set; }
    /// <summary>Move this person's open leads and tasks to another member (e.g. when they leave).</summary>
    public Guid? ReassignTo { get; set; }
}

public sealed record CrmTaskDto(Guid Id, string Title, CrmTaskType Type, DateTime DueAtUtc, Guid? AssignedToUserId, string? AssignedTo,
    Guid? LeadId, string? LeadLabel, Guid? TenantId, string? CustomerName, string? Notes, DateTime? CompletedAtUtc, string? Outcome,
    string? LeadPhone = null, string? LeadEmail = null, string? PlanKey = null);

public sealed class SaveCrmTaskRequest
{
    [Required, MaxLength(200)] public string Title { get; set; } = "";
    public CrmTaskType Type { get; set; } = CrmTaskType.FollowUp;
    public DateTime DueAtUtc { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? TenantId { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public sealed class CompleteCrmTaskRequest
{
    [MaxLength(1000)] public string? Outcome { get; set; }
    public bool Reopen { get; set; }
}

public sealed record CrmLeadBrief(Guid Id, string FullName, string AcademyName, string? Owner, DateTime? AtUtc, LeadStage Stage);
public sealed record CrmToday(List<CrmTaskDto> Overdue, List<CrmTaskDto> Today, List<CrmTaskDto> Upcoming, List<CrmLeadBrief> DemosToday, List<CrmLeadBrief> WaitingForReply, List<CrmLeadBrief> FollowUpsDue);

public class CrmLeadRow
{
    [MaxLength(150)] public string? FullName { get; set; }
    [MaxLength(256)] public string? Email { get; set; }
    [MaxLength(30)] public string? Phone { get; set; }
    [MaxLength(150)] public string? AcademyName { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(100)] public string? Region { get; set; }
    [MaxLength(100)] public string? Segment { get; set; }
    [MaxLength(300)] public string? Website { get; set; }
    public int? StudentCount { get; set; }
    [MaxLength(100)] public string? Source { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public sealed class CreateCrmLeadRequest : CrmLeadRow
{
    public Guid? AssignedToUserId { get; set; }
    public CrmLeadRow ToRow() => new()
    {
        FullName = FullName, Email = Email, Phone = Phone, AcademyName = AcademyName, City = City, Region = Region,
        Segment = Segment, Website = Website, StudentCount = StudentCount, Source = Source, Notes = Notes,
    };
}

public sealed class ImportCrmLeadsRequest
{
    /// <summary>Split the leads evenly between these people, in turn (managers only).</summary>
    public List<Guid> OwnerUserIds { get; set; } = [];
    /// <summary>Start this follow-up plan on every lead added.</summary>
    [MaxLength(40)] public string? FollowUpPlan { get; set; }
    public List<CrmLeadRow> Rows { get; set; } = [];
    public Guid? AssignedToUserId { get; set; }
    [MaxLength(100)] public string? Source { get; set; }
}

public sealed record CrmImportSkip(int Row, string Reason);
public sealed record CrmBulkResult(int Done, List<string> Skipped);

public sealed class BulkCrmLeadsRequest
{
    [Required, MaxLength(500)] public List<Guid> LeadIds { get; set; } = [];
    /// <summary>Assign the leads, split evenly between these people (managers only).</summary>
    public List<Guid> OwnerUserIds { get; set; } = [];
    [MaxLength(40)] public string? FollowUpPlan { get; set; }
}

public sealed class StartPlanRequest
{
    [Required, MaxLength(40)] public string Plan { get; set; } = "";
}
public sealed record CrmImportResult(int Created, List<Guid> CreatedIds, List<CrmImportSkip> Skipped);

public sealed class UpdateCrmDealRequest
{
    [MaxLength(50)] public string? ExpectedPlan { get; set; }
    public decimal? ExpectedMonthlyValue { get; set; }
    public DateTime? ExpectedCloseAtUtc { get; set; }
    public int? StudentCount { get; set; }
    [MaxLength(100)] public string? Region { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(100)] public string? Segment { get; set; }
    [MaxLength(300)] public string? Website { get; set; }
}

public sealed class LogCrmInteractionRequest
{
    [Required, RegularExpression("^(Call|Meeting|Email|WhatsApp)$")] public string Type { get; set; } = "Call";
    [Required, MaxLength(60)] public string Outcome { get; set; } = "";
    [MaxLength(800)] public string? Detail { get; set; }
    [MaxLength(200)] public string? NextStepTitle { get; set; }
    public DateTime? NextStepDueAtUtc { get; set; }
}

public sealed record CrmOwnerRow(Guid? OwnerId, string Owner, int Leads, int Contacted, double? MedianResponseHours, int Demos, int Trials,
    int Won, int Lost, decimal WonMonthlyValue, int OpenLeads, decimal OpenPipelineValue, int OverdueTasks, int TasksDone);
public sealed record CrmForecastStage(LeadStage Stage, string Label, int Count, decimal Value, double Probability, decimal Weighted);
public sealed record CrmForecastMonth(string Month, int Deals, decimal Weighted);
public sealed record CrmReport(int Days, List<CrmOwnerRow> Owners, List<CrmForecastStage> Forecast, decimal WeightedPipeline, int OpenWithoutValue,
    decimal WonMonthlyValue, List<CrmForecastMonth> ByMonth);

public sealed record CrmCustomerRow(Guid TenantId, string Name, string Code, string? Plan, string? Status, decimal MonthlyValue, int? Students, int? SessionsThisMonth,
    DateTime? TrialEndsAtUtc, DateTime? RenewsAtUtc, CustomerHealth Health, List<string> HealthReasons, string? Email, string? Owner, int OpenTasks, DateTime JoinedAtUtc);
public sealed record CrmInvoice(string Number, decimal Total, string Currency, string Status, DateTime IssuedAtUtc, DateTime DueAtUtc, DateTime? PaidAtUtc);
public sealed record CrmUsagePoint(DateOnly Date, int Students, int Staff, int SessionsThisMonth);
public sealed record CrmNote(Guid Id, string Text, string? Author, DateTime AtUtc);
public sealed record CrmCustomerDetail(CrmCustomerRow Customer, string? Phone, string? LegalName, List<CrmInvoice> Invoices, List<CrmUsagePoint> Usage,
    List<CrmLeadBrief> Leads, List<CrmNote> Notes, List<CrmTaskDto> Tasks);
