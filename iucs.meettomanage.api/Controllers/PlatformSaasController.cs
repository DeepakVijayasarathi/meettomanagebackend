using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.api.Saas;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Controllers;

public sealed class PriceInput
{
    public Guid? Id { get; set; }
    public BillingInterval BillingInterval { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "INR";
    [Range(0, 10_000_000)] public decimal Amount { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(128)] public string? GatewayPlanRef { get; set; }
}

public sealed class EntitlementInput
{
    [Required] public string Key { get; set; } = "";
    public long Value { get; set; }
}

public sealed class PlanSaveRequest
{
    [Required, RegularExpression("^[a-z][a-z0-9_-]{1,49}$")] public string Code { get; set; } = "";
    [Required, MaxLength(150)] public string Name { get; set; } = "";
    [MaxLength(1000)] public string? Description { get; set; }
    public bool IsPublic { get; set; } = true;
    public bool IsActive { get; set; } = true;
    [Range(0, 365)] public int TrialDays { get; set; }
    public int SortOrder { get; set; }
    public List<PriceInput> Prices { get; set; } = [];
    public List<EntitlementInput> Entitlements { get; set; } = [];
}

public sealed class AssignPlanRequest
{
    /// <summary>"trial", "complimentary" or "invoice".</summary>
    [Required] public string Mode { get; set; } = "invoice";
    public string? PlanCode { get; set; }
    public Guid? PlanPriceId { get; set; }
    public int? TrialDays { get; set; }
}

public sealed class OverrideInput
{
    [Required] public string Key { get; set; } = "";
    public long Value { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }
}

public sealed class TemplateSaveRequest
{
    [Required, MaxLength(300)] public string Subject { get; set; } = "";
    [Required] public string Body { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class BillingProfileRequest
{
    [MaxLength(200)] public string? LegalName { get; set; }
    [EmailAddress, MaxLength(256)] public string? BillingEmail { get; set; }
    [MaxLength(30)] public string? BillingPhone { get; set; }
    [MaxLength(20)] public string? Gstin { get; set; }
    [MaxLength(500)] public string? Address { get; set; }
    [MaxLength(60)] public string? State { get; set; }
}

/// <summary>The platform operator's view of the SaaS layer: plans, subscriptions, invoices, revenue and reminders.</summary>
[ApiController, Route("api/platform"), Authorize(Policy = "PlatformAdmin")]
public sealed class PlatformSaasController(MasterDbContext master, SaasSubscriptionService subscriptions, IEntitlementService entitlements,
    SaasLifecycleBackgroundService lifecycle, SaasUsageBackgroundService usageWorker, IConfiguration configuration) : ControllerBase
{
    private string Actor => "platform:" + User.Identity?.Name;

    // ------------------------------------------------------------------ plans

    [HttpGet("entitlement-catalog")]
    public IActionResult Catalog() => Ok(EntitlementKeys.Catalog.Select(x => new { x.Key, x.Label, type = x.Type.ToString(), x.Unit }));

    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct)
    {
        var plans = await master.Plans.AsNoTracking().Include(x => x.Prices).Include(x => x.Entitlements)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(ct);
        var subscribers = await master.TenantSubscriptions.AsNoTracking().GroupBy(x => x.PlanId)
            .Select(g => new { PlanId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.PlanId, x => x.Count, ct);
        return Ok(plans.Select(p => PlanView(p, subscribers.GetValueOrDefault(p.Id))));
    }

    [HttpPost("plans")]
    public async Task<IActionResult> CreatePlan(PlanSaveRequest request, CancellationToken ct)
    {
        if (await master.Plans.AnyAsync(x => x.Code == request.Code, ct)) throw new ConflictException($"A plan with code '{request.Code}' already exists.");
        var plan = new Plan { Code = request.Code };
        master.Plans.Add(plan);
        ApplyPlan(plan, request);
        await master.SaveChangesAsync(ct);
        entitlements.InvalidateAll();
        return Ok(PlanView(plan, 0));
    }

    [HttpPut("plans/{id:guid}")]
    public async Task<IActionResult> UpdatePlan(Guid id, PlanSaveRequest request, CancellationToken ct)
    {
        var plan = await master.Plans.Include(x => x.Prices).Include(x => x.Entitlements).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Plan", id);
        if (plan.Code != request.Code) throw new DomainValidationException("A plan's code cannot be changed.");
        ApplyPlan(plan, request);
        await master.SaveChangesAsync(ct);
        entitlements.InvalidateAll();
        return Ok(PlanView(plan, await master.TenantSubscriptions.CountAsync(x => x.PlanId == id, ct)));
    }

    private void ApplyPlan(Plan plan, PlanSaveRequest request)
    {
        plan.Name = request.Name.Trim(); plan.Description = request.Description?.Trim();
        plan.IsPublic = request.IsPublic; plan.IsActive = request.IsActive; plan.TrialDays = request.TrialDays;
        plan.SortOrder = request.SortOrder; plan.UpdatedAtUtc = DateTime.UtcNow;

        foreach (var input in request.Prices)
        {
            var existing = input.Id is { } priceId ? plan.Prices.SingleOrDefault(x => x.Id == priceId) : null;
            if (existing is null)
            {
                if (input.Id is not null) throw new DomainValidationException("Unknown price.");
                AddPrice(plan, new PlanPrice { BillingInterval = input.BillingInterval, Currency = input.Currency.ToUpperInvariant(), Amount = input.Amount, IsActive = input.IsActive, GatewayPlanRef = input.GatewayPlanRef });
            }
            else if (existing.Amount != input.Amount || existing.BillingInterval != input.BillingInterval)
            {
                // Grandfathering: subscribers keep the old price row; new buyers get a new one.
                existing.IsActive = false;
                AddPrice(plan, new PlanPrice { BillingInterval = input.BillingInterval, Currency = input.Currency.ToUpperInvariant(), Amount = input.Amount, IsActive = input.IsActive, GatewayPlanRef = input.GatewayPlanRef });
            }
            else { existing.IsActive = input.IsActive; existing.GatewayPlanRef = input.GatewayPlanRef; }
        }

        var keys = new HashSet<string>();
        foreach (var input in request.Entitlements)
        {
            var definition = EntitlementKeys.Find(input.Key) ?? throw new DomainValidationException($"Unknown entitlement '{input.Key}'.");
            if (!keys.Add(input.Key)) throw new DomainValidationException($"Entitlement '{input.Key}' is listed twice.");
            if (definition.Type == EntitlementValueType.Bool ? input.Value is not (0 or 1) : input.Value < -1)
                throw new DomainValidationException($"'{definition.Label}' has an invalid value.");
            var row = plan.Entitlements.SingleOrDefault(x => x.Key == input.Key);
            if (row is null) master.PlanEntitlements.Add(new PlanEntitlement { PlanId = plan.Id, Key = input.Key, ValueType = definition.Type, Value = input.Value });
            else row.Value = input.Value;
        }
        master.PlanEntitlements.RemoveRange(plan.Entitlements.Where(x => !keys.Contains(x.Key) && master.Entry(x).State != EntityState.Added).ToList());
    }

    /// <summary>
    /// New children get client-generated GUID keys, so adding them only to the parent's collection would make EF
    /// treat them as updates of existing rows. Adding through the DbSet marks them as inserts.
    /// </summary>
    private void AddPrice(Plan plan, PlanPrice price)
    {
        price.PlanId = plan.Id;
        master.PlanPrices.Add(price);
    }

    private static object PlanView(Plan p, int subscribers) => new
    {
        p.Id, p.Code, p.Name, p.Description, p.IsPublic, p.IsActive, p.TrialDays, p.SortOrder, subscribers,
        prices = p.Prices.OrderBy(x => x.BillingInterval).ThenByDescending(x => x.EffectiveFromUtc)
            .Select(x => new { x.Id, billingInterval = x.BillingInterval.ToString(), x.Currency, x.Amount, x.IsActive, x.GatewayPlanRef }),
        entitlements = p.Entitlements.OrderBy(x => x.Key).Select(x => new { x.Key, x.Value }),
    };

    // ------------------------------------------------------------------ subscriptions

    [HttpGet("subscriptions")]
    public async Task<IActionResult> Subscriptions([FromQuery] SaasSubscriptionStatus? status, CancellationToken ct)
    {
        var query = master.TenantSubscriptions.AsNoTracking().Include(x => x.Tenant).Include(x => x.Plan).Include(x => x.PlanPrice).AsQueryable();
        if (status is { } filter) query = query.Where(x => x.Status == filter);
        var rows = await query.OrderBy(x => x.Tenant.Name).ToListAsync(ct);

        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));
        var snapshots = await master.TenantUsageSnapshots.AsNoTracking().Where(x => x.SnapshotDate >= since).ToListAsync(ct);
        var latest = snapshots.GroupBy(x => x.TenantId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.SnapshotDate).First());
        var open = await master.PlatformInvoices.AsNoTracking().Where(x => x.Status == PlatformInvoiceStatus.Open)
            .GroupBy(x => x.TenantId).Select(g => new { TenantId = g.Key, Count = g.Count(), Total = g.Sum(i => i.Total) })
            .ToDictionaryAsync(x => x.TenantId, ct);

        return Ok(rows.Select(s => new
        {
            tenantId = s.TenantId, tenantCode = s.Tenant.Code, tenantName = s.Tenant.Name, tenantStatus = s.Tenant.Status.ToString(),
            s.Id, planCode = s.Plan.Code, planName = s.Plan.Name, status = s.Status.ToString(),
            price = s.PlanPrice is null ? null : new { s.PlanPrice.Amount, s.PlanPrice.Currency, interval = s.PlanPrice.BillingInterval.ToString() },
            s.TrialEndsAtUtc, s.CurrentPeriodEndUtc, s.GraceEndsAtUtc, s.CancelAtPeriodEnd, s.SuspendedByBilling,
            mrr = s.Status is SaasSubscriptionStatus.Active or SaasSubscriptionStatus.PastDue && s.PlanPrice is not null && s.CurrentPeriodEndUtc is not null
                ? SaasRules.MonthlyEquivalent(s.PlanPrice.Amount, s.PlanPrice.BillingInterval) : 0m,
            usage = latest.TryGetValue(s.TenantId, out var u) ? new { u.ActiveStudents, u.StaffCount, storageMb = (u.StorageBytes + 1048575) / 1048576, u.SessionMinutesMonth, date = u.SnapshotDate } : null,
            openInvoices = open.TryGetValue(s.TenantId, out var o) ? o.Count : 0,
            openAmount = open.TryGetValue(s.TenantId, out var o2) ? o2.Total : 0m,
        }));
    }

    [HttpGet("tenants/{tenantId:guid}/subscription")]
    public async Task<IActionResult> Subscription(Guid tenantId, CancellationToken ct)
    {
        var sub = await master.TenantSubscriptions.AsNoTracking().Include(x => x.Tenant).Include(x => x.Plan).Include(x => x.PlanPrice)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct) ?? throw new NotFoundException("This tenant has no subscription yet.");
        var events = await master.TenantSubscriptionEvents.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(50).ToListAsync(ct);
        var overrides = await master.TenantEntitlementOverrides.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(ct);
        var profile = await master.TenantBillingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var effective = await entitlements.GetAsync(tenantId, ct);
        return Ok(new
        {
            tenantId, tenantName = sub.Tenant.Name, tenantCode = sub.Tenant.Code, tenantStatus = sub.Tenant.Status.ToString(),
            planCode = sub.Plan.Code, planName = sub.Plan.Name, status = sub.Status.ToString(),
            sub.PlanPriceId, sub.TrialEndsAtUtc, sub.CurrentPeriodStartUtc, sub.CurrentPeriodEndUtc, sub.GraceEndsAtUtc,
            sub.CancelAtPeriodEnd, sub.SuspendedByBilling, sub.PendingPlanPriceId,
            events = events.Select(e => new { e.EventType, e.Detail, e.Actor, e.CreatedAtUtc }),
            overrides = overrides.Select(o => new { o.Key, o.Value, o.ExpiresAtUtc, o.Reason }),
            effective = effective.Values,
            profile = profile is null ? null : new { profile.LegalName, profile.BillingEmail, profile.BillingPhone, profile.Gstin, profile.Address, profile.State },
        });
    }

    [HttpPost("tenants/{tenantId:guid}/subscription/assign")]
    public async Task<IActionResult> Assign(Guid tenantId, AssignPlanRequest request, CancellationToken ct)
    {
        await RequireTenantAsync(tenantId, ct);
        await subscriptions.EnsureSubscriptionAsync(tenantId, SaasSeeder.LegacyPlanCode, Actor, ct: ct); // make sure a row exists
        return Ok(await subscriptions.AssignAsync(tenantId, request.Mode, request.PlanCode, request.PlanPriceId, request.TrialDays, Actor, ct));
    }

    public sealed class ExtendTrialRequest { [Range(1, 365)] public int Days { get; set; } }

    [HttpPost("tenants/{tenantId:guid}/subscription/extend-trial")]
    public async Task<IActionResult> ExtendTrial(Guid tenantId, ExtendTrialRequest request, CancellationToken ct)
    {
        await subscriptions.ExtendTrialAsync(tenantId, request.Days, Actor, ct);
        return NoContent();
    }

    public sealed class CancelRequest { public bool Immediate { get; set; } }

    [HttpPost("tenants/{tenantId:guid}/subscription/cancel")]
    public async Task<IActionResult> Cancel(Guid tenantId, CancelRequest request, CancellationToken ct)
    {
        await subscriptions.CancelAsync(tenantId, request.Immediate, Actor, ct);
        return NoContent();
    }

    [HttpPost("tenants/{tenantId:guid}/subscription/resume")]
    public async Task<IActionResult> Resume(Guid tenantId, CancellationToken ct) => Ok(await subscriptions.ResumeAsync(tenantId, Actor, ct));

    [HttpPut("tenants/{tenantId:guid}/entitlement-overrides")]
    public async Task<IActionResult> SetOverrides(Guid tenantId, List<OverrideInput> request, CancellationToken ct)
    {
        await RequireTenantAsync(tenantId, ct);
        var existing = await master.TenantEntitlementOverrides.Where(x => x.TenantId == tenantId).ToListAsync(ct);
        var seen = new HashSet<string>();
        foreach (var input in request)
        {
            var definition = EntitlementKeys.Find(input.Key) ?? throw new DomainValidationException($"Unknown entitlement '{input.Key}'.");
            if (!seen.Add(input.Key)) throw new DomainValidationException($"Entitlement '{input.Key}' is listed twice.");
            if (definition.Type == EntitlementValueType.Bool ? input.Value is not (0 or 1) : input.Value < -1)
                throw new DomainValidationException($"'{definition.Label}' has an invalid value.");
            var row = existing.SingleOrDefault(x => x.Key == input.Key);
            if (row is null)
                master.TenantEntitlementOverrides.Add(new TenantEntitlementOverride { TenantId = tenantId, Key = input.Key, ValueType = definition.Type, Value = input.Value, ExpiresAtUtc = input.ExpiresAtUtc, Reason = input.Reason });
            else { row.Value = input.Value; row.ExpiresAtUtc = input.ExpiresAtUtc; row.Reason = input.Reason; }
        }
        master.TenantEntitlementOverrides.RemoveRange(existing.Where(x => !seen.Contains(x.Key)));
        var sub = await master.TenantSubscriptions.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (sub is not null)
            master.TenantSubscriptionEvents.Add(new TenantSubscriptionEvent { TenantId = tenantId, SubscriptionId = sub.Id, EventType = "overrides_changed", Detail = $"{request.Count} override(s)", Actor = Actor });
        await master.SaveChangesAsync(ct);
        entitlements.Invalidate(tenantId);
        return NoContent();
    }

    [HttpPut("tenants/{tenantId:guid}/billing-profile")]
    public async Task<IActionResult> SetProfile(Guid tenantId, BillingProfileRequest request, CancellationToken ct)
    {
        await RequireTenantAsync(tenantId, ct);
        var profile = await master.TenantBillingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (profile is null) master.TenantBillingProfiles.Add(profile = new TenantBillingProfile { TenantId = tenantId });
        profile.LegalName = request.LegalName?.Trim(); profile.BillingEmail = request.BillingEmail?.Trim().ToLowerInvariant();
        profile.BillingPhone = request.BillingPhone?.Trim(); profile.Gstin = request.Gstin?.Trim().ToUpperInvariant();
        profile.Address = request.Address?.Trim(); profile.State = request.State?.Trim(); profile.UpdatedAtUtc = DateTime.UtcNow;
        await master.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task RequireTenantAsync(Guid tenantId, CancellationToken ct)
    {
        if (!await master.Tenants.AnyAsync(x => x.Id == tenantId, ct)) throw new NotFoundException("Tenant", tenantId);
    }

    // ------------------------------------------------------------------ invoices

    [HttpGet("invoices")]
    public async Task<IActionResult> Invoices([FromQuery] PlatformInvoiceStatus? status, [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        var query = master.PlatformInvoices.AsNoTracking().Include(x => x.Tenant).AsQueryable();
        if (status is { } s) query = query.Where(x => x.Status == s);
        if (tenantId is { } t) query = query.Where(x => x.TenantId == t);
        var rows = await query.OrderByDescending(x => x.IssuedAtUtc).Take(300).ToListAsync(ct);
        return Ok(rows.Select(x => new
        {
            x.Id, x.InvoiceNumber, x.TenantId, tenantName = x.Tenant.Name, tenantCode = x.Tenant.Code, status = x.Status.ToString(),
            x.Subtotal, x.TaxAmount, x.Total, x.Currency, x.IssuedAtUtc, x.DueAtUtc, x.PaidAtUtc, x.PeriodStartUtc, x.PeriodEndUtc, x.PaymentUrl,
            overdue = x.Status == PlatformInvoiceStatus.Open && x.DueAtUtc < DateTime.UtcNow,
        }));
    }

    public sealed class MarkPaidRequest { [MaxLength(128)] public string? Reference { get; set; } }

    [HttpPost("invoices/{id:guid}/mark-paid")]
    public async Task<IActionResult> MarkPaid(Guid id, MarkPaidRequest request, CancellationToken ct)
    {
        var applied = await subscriptions.ApplyPaymentAsync(id, "manual", request.Reference, null, Actor, ct);
        return applied ? NoContent() : throw new ConflictException("The invoice is already paid.");
    }

    [HttpPost("invoices/{id:guid}/void")]
    public async Task<IActionResult> Void(Guid id, CancellationToken ct)
    {
        await subscriptions.VoidInvoiceAsync(id, Actor, ct);
        return NoContent();
    }

    [HttpPost("invoices/{id:guid}/payment-link")]
    public async Task<IActionResult> PaymentLink(Guid id, CancellationToken ct) =>
        Ok(new { paymentUrl = await subscriptions.EnsurePaymentLinkAsync(id, null, ct) });

    [HttpGet("invoices/{id:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid id, [FromServices] Microsoft.Extensions.Options.IOptions<SaasOptions> options, CancellationToken ct)
    {
        var invoice = await master.PlatformInvoices.AsNoTracking().Include(x => x.Lines).Include(x => x.Tenant)
            .SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Invoice", id);
        var profile = await master.TenantBillingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == invoice.TenantId, ct);
        return File(PlatformInvoicePdf.Render(invoice, invoice.Tenant, profile, options.Value), "application/pdf", $"{invoice.InvoiceNumber}.pdf");
    }

    // ------------------------------------------------------------------ revenue

    [HttpGet("revenue")]
    public async Task<IActionResult> Revenue(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since30 = now.AddDays(-30);
        var since90 = now.AddDays(-90);
        var subs = await master.TenantSubscriptions.AsNoTracking().Include(x => x.Plan).Include(x => x.PlanPrice).ToListAsync(ct);

        var billable = subs.Where(s => s.Status is SaasSubscriptionStatus.Active or SaasSubscriptionStatus.PastDue
            && s.PlanPrice is not null && s.CurrentPeriodEndUtc is not null).ToList();
        var mrr = billable.Sum(s => SaasRules.MonthlyEquivalent(s.PlanPrice!.Amount, s.PlanPrice.BillingInterval));

        var trialsStarted = await master.TenantSubscriptionEvents.CountAsync(x => x.EventType == "trial_started" && x.CreatedAtUtc >= since90, ct);
        var converted = await master.TenantSubscriptionEvents.CountAsync(x => x.EventType == "converted" && x.CreatedAtUtc >= since90, ct);
        var churned = await master.TenantSubscriptionEvents.CountAsync(x => x.EventType == "cancelled" && x.CreatedAtUtc >= since30, ct);
        var newPaid = await master.TenantSubscriptionEvents.CountAsync(x => x.EventType == "converted" && x.CreatedAtUtc >= since30, ct);
        var failed = await master.PlatformPayments.CountAsync(x => x.Status == PlatformPaymentStatus.Failed && x.CreatedAtUtc >= since30, ct);
        var collected = await master.PlatformInvoices.Where(x => x.Status == PlatformInvoiceStatus.Paid && x.PaidAtUtc >= since30).SumAsync(x => (decimal?)x.Total, ct) ?? 0m;
        var overdue = await master.PlatformInvoices.Where(x => x.Status == PlatformInvoiceStatus.Open && x.DueAtUtc < now).SumAsync(x => (decimal?)x.Total, ct) ?? 0m;

        var activeStart = billable.Count + churned;
        return Ok(new
        {
            mrr, arr = mrr * 12m, payingTenants = billable.Count,
            arpa = billable.Count == 0 ? 0m : SaasRules.Round(mrr / billable.Count),
            trialing = subs.Count(s => s.Status == SaasSubscriptionStatus.Trialing),
            pastDue = subs.Count(s => s.Status == SaasSubscriptionStatus.PastDue),
            suspended = subs.Count(s => s.Status == SaasSubscriptionStatus.Suspended),
            expired = subs.Count(s => s.Status == SaasSubscriptionStatus.Expired),
            cancelled = subs.Count(s => s.Status == SaasSubscriptionStatus.Cancelled),
            newPaid30d = newPaid, churned30d = churned,
            churnRatePercent = activeStart == 0 ? 0m : SaasRules.Round(churned * 100m / activeStart),
            trialConversionPercent = trialsStarted == 0 ? 0m : SaasRules.Round(converted * 100m / trialsStarted),
            failedPayments30d = failed, collected30d = collected, overdueAmount = overdue,
            planDistribution = subs.GroupBy(s => s.Plan.Name).Select(g => new { plan = g.Key, count = g.Count() }).OrderByDescending(x => x.count),
        });
    }

    // ------------------------------------------------------------------ reminders & templates

    [HttpGet("reminders")]
    public async Task<IActionResult> Reminders([FromQuery] int take = 200, CancellationToken ct = default)
    {
        var rows = await (from log in master.SubscriptionReminderLogs.AsNoTracking()
                          join tenant in master.Tenants.AsNoTracking() on log.TenantId equals tenant.Id
                          orderby log.CreatedAtUtc descending
                          select new { log.Id, tenantName = tenant.Name, tenantCode = tenant.Code, log.RuleKey, log.CycleKey, log.Channel, log.Recipient, status = log.Status.ToString(), log.Attempts, log.Error, log.CreatedAtUtc, log.SentAtUtc })
            .Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("email-templates")]
    public async Task<IActionResult> Templates(CancellationToken ct)
    {
        var rows = await master.PlatformEmailTemplates.AsNoTracking().ToListAsync(ct);
        return Ok(DefaultEmailTemplates.All.Select(d =>
        {
            var row = rows.SingleOrDefault(x => x.Key == d.Key);
            return new { d.Key, d.Title, subject = row?.Subject ?? d.Subject, body = row?.Body ?? d.Body, isActive = row?.IsActive ?? true };
        }));
    }

    [HttpPut("email-templates/{key}")]
    public async Task<IActionResult> SaveTemplate(string key, TemplateSaveRequest request, CancellationToken ct)
    {
        if (DefaultEmailTemplates.All.All(x => x.Key != key)) throw new NotFoundException("Template not found.");
        var row = await master.PlatformEmailTemplates.SingleOrDefaultAsync(x => x.Key == key, ct);
        if (row is null) master.PlatformEmailTemplates.Add(row = new PlatformEmailTemplate { Key = key, Subject = "", Body = "" });
        row.Subject = request.Subject.Trim(); row.Body = request.Body; row.IsActive = request.IsActive; row.UpdatedAtUtc = DateTime.UtcNow;
        await master.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Runs the billing cycle and usage collection now instead of waiting for the next scheduled pass.</summary>
    [HttpPost("billing/run-cycle")]
    public async Task<IActionResult> RunCycle(CancellationToken ct)
    {
        var billing = await lifecycle.RunOnceAsync(ct);
        var usage = await usageWorker.RunOnceAsync(ct);
        return Ok(new { billingRan = billing, usageRan = usage });
    }

    [HttpGet("billing/settings")]
    public IActionResult Settings() => Ok(new
    {
        gateway = configuration["SaasBilling:Gateway"] ?? "Manual",
        smtpConfigured = !string.IsNullOrWhiteSpace(configuration["SaasBilling:Smtp:Host"]),
        signupEnabled = configuration.GetValue<bool>("SaasBilling:SignupEnabled"),
        graceDays = configuration.GetValue("SaasBilling:GraceDays", 7),
        taxRatePercent = configuration.GetValue("SaasBilling:TaxRatePercent", 18m),
    });
}
