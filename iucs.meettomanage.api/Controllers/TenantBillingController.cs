using iucs.meettomanage.api.Saas;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Controllers;

public sealed class ChangePlanRequest { public Guid PlanPriceId { get; set; } }

/// <summary>
/// The client administrator's own subscription page: plan, usage against limits, invoices, upgrade/downgrade/cancel.
/// Distinct from /api/subscriptions, which is the academy billing parents.
/// </summary>
[ApiController, Route("api/saas-billing"), Authorize(Roles = nameof(UserRole.Admin))]
public sealed class TenantBillingController(MasterDbContext master, TenantContext tenant, MeetToManageDbContext business,
    SaasSubscriptionService subscriptions, IEntitlementService entitlements, IOptions<SaasOptions> options) : ControllerBase
{
    private Guid TenantId => tenant.TenantId ?? throw new ForbiddenException("A tenant-bound session is required.");

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var tenantId = TenantId;
        var sub = await master.TenantSubscriptions.AsNoTracking().Include(x => x.Plan).Include(x => x.PlanPrice)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (sub is null) return Ok(new { managed = false });

        var now = DateTime.UtcNow;
        var granted = await entitlements.GetAsync(tenantId, ct);
        var used = await SaasUsageService.ComputeAsync(business, now, ct);
        var pending = sub.PendingPlanPriceId is { } pendingId
            ? await master.PlanPrices.AsNoTracking().Include(x => x.Plan).SingleOrDefaultAsync(x => x.Id == pendingId, ct) : null;
        var open = await master.PlatformInvoices.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status == PlatformInvoiceStatus.Open)
            .OrderBy(x => x.DueAtUtc).ToListAsync(ct);
        var banner = SaasRules.Banner(sub, now);

        var limits = SaasUsageService.MeteredKeys.Select(key =>
        {
            var definition = EntitlementKeys.Find(key)!;
            var limit = granted.Limit(key);
            var current = used.For(key) ?? 0;
            return new
            {
                key, label = definition.Label, unit = definition.Unit, used = current, limit,
                percent = limit <= 0 ? 0 : (int)Math.Min(100, current * 100 / limit),
            };
        });
        var features = EntitlementKeys.Catalog.Where(x => x.Type == EntitlementValueType.Bool)
            .Select(x => new { x.Key, x.Label, enabled = granted.Can(x.Key) });

        return Ok(new
        {
            managed = true,
            planCode = sub.Plan.Code, planName = sub.Plan.Name, status = sub.Status.ToString(),
            price = sub.PlanPrice is null ? null : new { sub.PlanPrice.Amount, sub.PlanPrice.Currency, interval = sub.PlanPrice.BillingInterval.ToString() },
            sub.PlanPriceId, sub.TrialEndsAtUtc, sub.CurrentPeriodEndUtc, sub.GraceEndsAtUtc, sub.CancelAtPeriodEnd,
            pendingPlan = pending is null ? null : new { pending.Plan.Name, pending.Amount, interval = pending.BillingInterval.ToString() },
            banner = banner is null ? null : new { level = banner.Value.Level, message = banner.Value.Message },
            limits, features,
            openInvoices = open.Select(x => new { x.Id, x.InvoiceNumber, x.Total, x.Currency, x.DueAtUtc, x.PaymentUrl }),
            taxRatePercent = options.Value.TaxRatePercent,
        });
    }

    /// <summary>Compact status for the app-wide banner; cheap, no usage scan.</summary>
    [HttpGet("banner")]
    public async Task<IActionResult> Banner(CancellationToken ct)
    {
        var sub = await master.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == TenantId, ct);
        var banner = sub is null ? null : SaasRules.Banner(sub, DateTime.UtcNow);
        return Ok(new { banner = banner is null ? null : new { level = banner.Value.Level, message = banner.Value.Message } });
    }

    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct)
    {
        var plans = await master.Plans.AsNoTracking().Include(x => x.Prices).Include(x => x.Entitlements)
            .Where(x => x.IsPublic && x.IsActive).OrderBy(x => x.SortOrder).ToListAsync(ct);
        return Ok(plans.Select(p => new
        {
            p.Id, p.Code, p.Name, p.Description,
            prices = p.Prices.Where(x => x.IsActive).OrderBy(x => x.BillingInterval)
                .Select(x => new { x.Id, billingInterval = x.BillingInterval.ToString(), x.Currency, x.Amount }),
            entitlements = p.Entitlements.Select(x => new { x.Key, x.Value }),
        }));
    }

    [HttpPost("change-plan")]
    public async Task<IActionResult> ChangePlan(ChangePlanRequest request, CancellationToken ct) =>
        Ok(await subscriptions.ChangePlanAsync(TenantId, request.PlanPriceId, "tenant", false, ct));

    public sealed class CancelBody { public bool Immediate { get; set; } }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(CancelBody body, CancellationToken ct)
    {
        // A tenant always cancels at the end of what it has paid for; an immediate stop is an operator action.
        await subscriptions.CancelAsync(TenantId, false, "tenant", ct);
        return NoContent();
    }

    [HttpPost("resume")]
    public async Task<IActionResult> Resume(CancellationToken ct) => Ok(await subscriptions.ResumeAsync(TenantId, "tenant", ct));

    [HttpGet("invoices")]
    public async Task<IActionResult> Invoices(CancellationToken ct)
    {
        var rows = await master.PlatformInvoices.AsNoTracking().Where(x => x.TenantId == TenantId)
            .OrderByDescending(x => x.IssuedAtUtc).Take(100).ToListAsync(ct);
        return Ok(rows.Select(x => new
        {
            x.Id, x.InvoiceNumber, status = x.Status.ToString(), x.Subtotal, x.TaxAmount, x.Total, x.Currency,
            x.IssuedAtUtc, x.DueAtUtc, x.PaidAtUtc, x.PeriodStartUtc, x.PeriodEndUtc, x.PaymentUrl,
        }));
    }

    [HttpPost("invoices/{id:guid}/pay-link")]
    public async Task<IActionResult> PayLink(Guid id, CancellationToken ct) =>
        Ok(new { paymentUrl = await subscriptions.EnsurePaymentLinkAsync(id, TenantId, ct) });

    [HttpGet("invoices/{id:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid id, CancellationToken ct)
    {
        var invoice = await master.PlatformInvoices.AsNoTracking().Include(x => x.Lines).Include(x => x.Tenant)
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == TenantId, ct) ?? throw new NotFoundException("Invoice", id);
        var profile = await master.TenantBillingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == TenantId, ct);
        return File(PlatformInvoicePdf.Render(invoice, invoice.Tenant, profile, options.Value), "application/pdf", $"{invoice.InvoiceNumber}.pdf");
    }

    [HttpGet("profile")]
    public async Task<IActionResult> Profile(CancellationToken ct)
    {
        var p = await master.TenantBillingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == TenantId, ct);
        return Ok(new { p?.LegalName, p?.BillingEmail, p?.BillingPhone, p?.Gstin, p?.Address, p?.State });
    }

    [HttpPut("profile")]
    public async Task<IActionResult> SaveProfile(BillingProfileRequest request, CancellationToken ct)
    {
        var tenantId = TenantId;
        var profile = await master.TenantBillingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (profile is null) master.TenantBillingProfiles.Add(profile = new TenantBillingProfile { TenantId = tenantId });
        profile.LegalName = request.LegalName?.Trim(); profile.BillingEmail = request.BillingEmail?.Trim().ToLowerInvariant();
        profile.BillingPhone = request.BillingPhone?.Trim(); profile.Gstin = request.Gstin?.Trim().ToUpperInvariant();
        profile.Address = request.Address?.Trim(); profile.State = request.State?.Trim(); profile.UpdatedAtUtc = DateTime.UtcNow;
        await master.SaveChangesAsync(ct);
        return NoContent();
    }
}
