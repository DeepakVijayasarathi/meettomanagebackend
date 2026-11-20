using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Common;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Marketing;

/// <summary>
/// The signed-in marketing user's CRM role and what it allows. Managers see and change everything; sales reps
/// work their own leads plus unassigned ones; viewers read only. A marketing user without a team row is a Manager,
/// so the original account keeps full access.
/// </summary>
public sealed class CrmAccess(MeetToManageDbContext db, ICurrentUserService current)
{
    private CrmRole? _role;

    public Guid? UserId => current.UserId;

    public async Task<CrmRole> RoleAsync(CancellationToken ct = default)
    {
        if (_role is { } cached) return cached;
        if (current.UserId is not { } id) return (_role = CrmRole.Viewer).Value;
        var row = await db.CrmTeamMembers.AsNoTracking().Where(x => x.UserId == id).Select(x => (CrmRole?)x.Role).FirstOrDefaultAsync(ct);
        return (_role = row ?? CrmRole.Manager).Value;
    }

    public async Task RequireWriteAsync(CancellationToken ct = default)
    {
        if (await RoleAsync(ct) == CrmRole.Viewer) throw new ForbiddenException("Your CRM role is view-only.");
    }

    public async Task RequireManagerAsync(CancellationToken ct = default)
    {
        if (await RoleAsync(ct) != CrmRole.Manager) throw new ForbiddenException("Only a CRM manager can do this.");
    }

    /// <summary>For a sales rep, the user whose leads they may see (their own plus unassigned); null means everything.</summary>
    public async Task<Guid?> VisibleToAsync(CancellationToken ct = default) =>
        await RoleAsync(ct) == CrmRole.Sales ? current.UserId : null;

    /// <summary>Leads and tasks can only be owned by active managers and sales reps, never by viewers.</summary>
    public async Task EnsureCanOwnAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await CanOwnAsync(userId, ct))
            throw new application.Common.Exceptions.DomainValidationException("Leads and tasks can only be given to an active manager or sales rep.");
    }

    public async Task<bool> CanOwnAsync(Guid userId, CancellationToken ct = default) =>
        await db.Users.AnyAsync(x => x.Id == userId && x.Role == domain.Enums.UserRole.Marketing && x.Status == domain.Enums.UserStatus.Active, ct)
        && !await db.CrmTeamMembers.AnyAsync(x => x.UserId == userId && x.Role == CrmRole.Viewer, ct);

    public static bool CanSee(Guid? visibleTo, Guid? assignedTo) => visibleTo is null || assignedTo is null || assignedTo == visibleTo;

    public async Task EnsureCanSeeLeadAsync(Guid leadId, CancellationToken ct = default)
    {
        if (await VisibleToAsync(ct) is not { } me) return;
        var owner = await db.PlatformDemoRequests.AsNoTracking().Where(x => x.Id == leadId).Select(x => new { x.AssignedToUserId }).FirstOrDefaultAsync(ct);
        if (owner is not null && !CanSee(me, owner.AssignedToUserId)) throw new ForbiddenException("This lead belongs to another salesperson.");
    }

    public Task<bool> IsDoNotContactAsync(Guid leadId, CancellationToken ct = default) =>
        db.PlatformDemoRequests.AsNoTracking().AnyAsync(x => x.Id == leadId && x.Stage == LeadStage.Lost && x.LostReason == "Do not contact", ct);

    public Task<bool> AnyDoNotContactAsync(IEnumerable<Guid> leadIds, CancellationToken ct = default)
    {
        var ids = leadIds.Distinct().ToList();
        return db.PlatformDemoRequests.AsNoTracking().AnyAsync(x => ids.Contains(x.Id) && x.Stage == LeadStage.Lost && x.LostReason == "Do not contact", ct);
    }
}

/// <summary>
/// Applies CRM roles to the existing lead endpoints (Demo Requests) without changing each action: viewers can only
/// read, sales reps only reach their own or unassigned leads and can't reassign to others, and only managers may
/// delete leads or change campaign budgets and report settings.
/// </summary>
public sealed class CrmLeadAccessFilter(CrmAccess access) : IAsyncActionFilter
{
    private static readonly HashSet<string> ManagerOnly = ["Delete", "SaveCampaign", "DeleteCampaign", "SaveSettings"];

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ct = context.HttpContext.RequestAborted;
        var action = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName ?? "";
        var role = await access.RoleAsync(ct);
        var isRead = HttpMethods.IsGet(context.HttpContext.Request.Method);

        if (role != CrmRole.Manager && context.ActionArguments.TryGetValue("id", out var protectedId) && protectedId is Guid protectedLeadId)
        {
            var reopensOptOut = context.ActionArguments.TryGetValue("request", out var protectedBody) && protectedBody switch
            {
                SetLeadStageRequest s => s.Stage != LeadStage.Lost,
                UpdatePlatformDemoRequestStatusRequest s => s.Status != StoreInquiryStatus.Closed,
                UpdatePlatformDemoRequestRequest s => s.Status.HasValue && s.Status != StoreInquiryStatus.Closed,
                _ => false,
            };
            if (reopensOptOut && await access.IsDoNotContactAsync(protectedLeadId, ct))
                await access.RequireManagerAsync(ct);
        }

        if (role != CrmRole.Manager && context.ActionArguments.TryGetValue("request", out var bulkBody)
            && bulkBody is BulkMarketingLeadRequest { Status: { } status } protectedBulk && status != StoreInquiryStatus.Closed
            && await access.AnyDoNotContactAsync(protectedBulk.Ids, ct))
            await access.RequireManagerAsync(ct);

        if (context.ActionArguments.TryGetValue("query", out var q) && q is MarketingLeadQuery query)
            query.VisibleToUserId = role == CrmRole.Sales ? access.UserId : null;

        if (context.ActionArguments.TryGetValue("request", out var assignBody))
        {
            var assignee = assignBody switch
            {
                UpdatePlatformDemoRequestRequest u => u.AssignedToUserId,
                BulkMarketingLeadRequest b => b.AssignedToUserId,
                _ => null,
            };
            if (assignee is { } to) await access.EnsureCanOwnAsync(to, ct);
        }

        if (role == CrmRole.Manager) { await next(); return; }
        if (!isRead) await access.RequireWriteAsync(ct);
        if (ManagerOnly.Contains(action)) await access.RequireManagerAsync(ct);

        if (role == CrmRole.Sales)
        {
            var me = access.UserId;
            if (action == "List") throw new application.Common.Exceptions.ForbiddenException("Use the leads list.");
            if (context.ActionArguments.TryGetValue("id", out var idArg) && idArg is Guid id && action is not ("DeleteCampaign"))
                await access.EnsureCanSeeLeadAsync(id, ct);
            if (context.ActionArguments.TryGetValue("request", out var body))
            {
                if (body is UpdatePlatformDemoRequestRequest update && update.AssignedToUserId is { } to && to != me)
                    throw new application.Common.Exceptions.ForbiddenException("Only a CRM manager can assign leads to someone else.");
                if (body is BulkMarketingLeadRequest bulk)
                {
                    if (bulk.AssignedToUserId is { } bto && bto != me)
                        throw new application.Common.Exceptions.ForbiddenException("Only a CRM manager can assign leads to someone else.");
                    foreach (var leadId in bulk.Ids) await access.EnsureCanSeeLeadAsync(leadId, ct);
                }
            }
        }
        await next();
    }
}
