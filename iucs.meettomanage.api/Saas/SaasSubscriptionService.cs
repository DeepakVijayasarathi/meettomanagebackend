using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Saas;

public sealed record PlanChangeResult(string Outcome, Guid? InvoiceId, string? PaymentUrl, string Message);

/// <summary>
/// Every subscription transition lives here: starting a trial, buying/upgrading/downgrading a plan, cancelling,
/// settling payments and the periodic billing cycle. Each transition writes a tenant_subscription_event, keeps the
/// tenant's login status in step (Tenant.Status), refreshes the entitlement cache and notifies the tenant.
/// </summary>
public sealed class SaasSubscriptionService(IDbContextFactory<MasterDbContext> factory, IEntitlementService entitlements,
    SaasReminderService reminders, SaasUsageService usage, IPlatformPaymentGateway gateway, IOptions<SaasOptions> options,
    ILogger<SaasSubscriptionService> logger)
{
    private SaasOptions Options => options.Value;

    // ---------------------------------------------------------------- creation

    /// <summary>Gives a tenant its subscription row if it has none. Safe to call repeatedly.</summary>
    public async Task<TenantSubscription> EnsureSubscriptionAsync(Guid tenantId, string? planCode, string actor,
        string? billingEmail = null, string? legalName = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.TenantSubscriptions.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (existing is not null)
        {
            await SaveBillingProfileAsync(db, tenantId, billingEmail, legalName, ct);
            return existing;
        }
        var code = string.IsNullOrWhiteSpace(planCode) ? Options.DefaultPlanForNewTenants : planCode.Trim().ToLowerInvariant();
        var plan = await db.Plans.SingleOrDefaultAsync(x => x.Code == code && x.IsActive, ct)
            ?? throw new DomainValidationException($"Plan '{code}' does not exist or is inactive.");
        var now = DateTime.UtcNow;
        var sub = new TenantSubscription { TenantId = tenantId, PlanId = plan.Id, Status = SaasSubscriptionStatus.Active };
        if (plan.TrialDays > 0)
        {
            sub.Status = SaasSubscriptionStatus.Trialing;
            sub.TrialEndsAtUtc = now.AddDays(plan.TrialDays);
        }
        db.TenantSubscriptions.Add(sub);
        AddEvent(db, sub, plan.TrialDays > 0 ? "trial_started" : "subscription_created", $"Plan {plan.Code}", actor);
        await SaveBillingProfileAsync(db, tenantId, billingEmail, legalName, ct, save: false);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) // another instance created it first
        {
            db.ChangeTracker.Clear();
            return await db.TenantSubscriptions.SingleAsync(x => x.TenantId == tenantId, ct);
        }
        entitlements.Invalidate(tenantId);
        if (sub.Status == SaasSubscriptionStatus.Trialing)
            await reminders.NotifyAsync(tenantId, "trial_started", sub.Id.ToString("N"), ct: ct);
        return sub;
    }

    private static async Task SaveBillingProfileAsync(MasterDbContext db, Guid tenantId, string? email, string? legalName, CancellationToken ct, bool save = true)
    {
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(legalName)) return;
        var profile = await db.TenantBillingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (profile is null)
            db.TenantBillingProfiles.Add(profile = new TenantBillingProfile { TenantId = tenantId });
        if (!string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(profile.BillingEmail)) profile.BillingEmail = email.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(legalName) && string.IsNullOrWhiteSpace(profile.LegalName)) profile.LegalName = legalName.Trim();
        if (save) await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- operator assignment

    /// <summary>Operator assignment. mode: "trial", "complimentary" (no billing) or "invoice" (customer must pay).</summary>
    public async Task<PlanChangeResult> AssignAsync(Guid tenantId, string mode, string? planCode, Guid? planPriceId, int? trialDays,
        string actor, CancellationToken ct)
    {
        if (mode == "invoice")
            return await ChangePlanAsync(tenantId, planPriceId ?? throw new DomainValidationException("Choose a plan price."), actor, true, ct);

        await using var db = await factory.CreateDbContextAsync(ct);
        var sub = await RequireSubscriptionAsync(db, tenantId, ct);
        var now = DateTime.UtcNow;
        if (mode == "trial")
        {
            var plan = await db.Plans.SingleOrDefaultAsync(x => x.Code == (planCode ?? "trial") && x.IsActive, ct)
                ?? throw new DomainValidationException("Plan not found.");
            var days = trialDays ?? (plan.TrialDays > 0 ? plan.TrialDays : Options.DefaultTrialDays);
            sub.PlanId = plan.Id; sub.PlanPriceId = null; sub.Status = SaasSubscriptionStatus.Trialing;
            sub.TrialEndsAtUtc = now.AddDays(days);
            sub.CurrentPeriodStartUtc = sub.CurrentPeriodEndUtc = sub.GraceEndsAtUtc = null;
            sub.CancelAtPeriodEnd = false; sub.PendingPlanPriceId = null;
            AddEvent(db, sub, "trial_started", $"{days}-day trial on {plan.Code}", actor);
        }
        else if (mode == "complimentary")
        {
            PlanPrice? price = null;
            Plan plan;
            if (planPriceId is { } priceId)
            {
                price = await db.PlanPrices.Include(x => x.Plan).SingleOrDefaultAsync(x => x.Id == priceId, ct)
                    ?? throw new NotFoundException("Plan price", priceId);
                plan = price.Plan;
            }
            else plan = await db.Plans.SingleOrDefaultAsync(x => x.Code == planCode && x.IsActive, ct)
                ?? throw new DomainValidationException("Choose a plan.");
            ApplyNoRenewalPlan(sub, plan, price);
            AddEvent(db, sub, "plan_assigned", $"Complimentary {plan.Code}", actor);
        }
        else throw new DomainValidationException("Mode must be trial, complimentary or invoice.");

        var reactivated = await SyncTenantAsync(db, sub, suspend: false, ct);
        await db.SaveChangesAsync(ct);
        entitlements.Invalidate(tenantId);
        if (reactivated) await reminders.NotifyAsync(tenantId, "reactivated", Guid.NewGuid().ToString("N"), ct: ct);
        return new PlanChangeResult("applied", null, null, "Plan assigned.");
    }

    private static void ApplyNoRenewalPlan(TenantSubscription sub, Plan plan, PlanPrice? price)
    {
        sub.PlanId = plan.Id; sub.PlanPriceId = price?.Id; sub.Status = SaasSubscriptionStatus.Active;
        sub.TrialEndsAtUtc = sub.CurrentPeriodStartUtc = sub.CurrentPeriodEndUtc = sub.GraceEndsAtUtc = null;
        sub.CancelAtPeriodEnd = false; sub.CancelledAtUtc = null; sub.PendingPlanPriceId = null;
    }

    // ---------------------------------------------------------------- tenant/operator plan change

    /// <summary>
    /// Buy, upgrade or downgrade. Upgrades and first purchases produce an invoice (new price less credit for unused time)
    /// that activates on payment; downgrades are scheduled for the next renewal after checking current usage fits.
    /// </summary>
    public async Task<PlanChangeResult> ChangePlanAsync(Guid tenantId, Guid planPriceId, string actor, bool isOperator, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var price = await db.PlanPrices.Include(x => x.Plan).ThenInclude(x => x.Entitlements)
            .SingleOrDefaultAsync(x => x.Id == planPriceId, ct) ?? throw new NotFoundException("Plan price", planPriceId);
        if (!price.IsActive || !price.Plan.IsActive || (!isOperator && !price.Plan.IsPublic))
            throw new DomainValidationException("That plan is not available.");
        var sub = await RequireSubscriptionAsync(db, tenantId, ct);
        var tenant = await db.Tenants.SingleAsync(x => x.Id == tenantId, ct);
        var now = DateTime.UtcNow;
        var current = sub.PlanPriceId is { } currentId ? await db.PlanPrices.Include(x => x.Plan).SingleAsync(x => x.Id == currentId, ct) : null;

        if (current?.Id == price.Id && sub.Status is SaasSubscriptionStatus.Active or SaasSubscriptionStatus.PastDue)
        {
            if (sub.CancelAtPeriodEnd) return await ResumeAsync(tenantId, actor, ct);
            throw new ConflictException("You are already on this plan.");
        }

        if (price.Amount <= 0)
        {
            ApplyNoRenewalPlan(sub, price.Plan, price);
            AddEvent(db, sub, "plan_changed", $"Moved to free price of {price.Plan.Code}", actor);
            await SyncTenantAsync(db, sub, false, ct);
            await db.SaveChangesAsync(ct);
            entitlements.Invalidate(tenantId);
            return new PlanChangeResult("applied", null, null, "Your plan has been changed.");
        }

        var hasPaidPeriod = current is not null && sub.Status is SaasSubscriptionStatus.Active or SaasSubscriptionStatus.PastDue
            && sub.CurrentPeriodStartUtc is not null && sub.CurrentPeriodEndUtc > now;
        decimal credit = 0m;
        if (hasPaidPeriod)
        {
            var downgrade = SaasRules.MonthlyEquivalent(price.Amount, price.BillingInterval) < SaasRules.MonthlyEquivalent(current!.Amount, current.BillingInterval);
            if (downgrade)
            {
                await EnsureFitsAsync(tenantId, price, ct);
                sub.PendingPlanPriceId = price.Id;
                AddEvent(db, sub, "downgrade_scheduled", $"To {price.Plan.Code} at renewal", actor);
                await db.SaveChangesAsync(ct);
                await reminders.NotifyAsync(tenantId, "downgrade_scheduled", price.Id.ToString("N") + sub.CurrentPeriodEndUtc!.Value.ToString("yyyyMMdd"),
                    extra: new Dictionary<string, string> { ["plan_name"] = price.Plan.Name }, ct: ct);
                return new PlanChangeResult("scheduled", null, null, $"Your plan will change to {price.Plan.Name} on {sub.CurrentPeriodEndUtc:dd MMM yyyy}.");
            }
            credit = SaasRules.UnusedCredit(current!.Amount, sub.CurrentPeriodStartUtc!.Value, sub.CurrentPeriodEndUtc!.Value, now);
        }

        var charge = SaasRules.UpgradeCharge(price.Amount, credit);
        var interval = price.BillingInterval == BillingInterval.Year ? "yearly" : "monthly";
        var lines = new List<PlatformInvoiceLine>
        {
            new() { Description = $"{price.Plan.Name} plan ({interval})", Quantity = 1, UnitAmount = price.Amount, Amount = price.Amount },
        };
        if (credit > 0)
            lines.Add(new() { Description = $"Credit for unused time on {current!.Plan.Name}", Quantity = 1, UnitAmount = -credit, Amount = -credit });

        // A new purchase replaces any other unpaid invoice (it would otherwise be payable twice).
        await VoidOpenInvoicesAsync(db, tenantId, ct);
        var invoice = await BuildInvoiceAsync(db, tenant, sub, lines, now, SaasRules.AddInterval(now, price.BillingInterval),
            now.AddDays(Options.InvoiceLeadDays), price, ct);
        AddEvent(db, sub, "invoice_issued", $"{invoice.InvoiceNumber} {invoice.Currency} {invoice.Total} for {price.Plan.Code}", actor);
        await db.SaveChangesAsync(ct);

        if (invoice.Total <= 0)
        {
            await ApplyPaymentAsync(invoice.Id, "credit", null, null, actor, ct);
            return new PlanChangeResult("applied", invoice.Id, null, "Your plan has been upgraded using your remaining credit.");
        }
        await AttachPaymentLinkAsync(db, invoice, tenant, ct);
        await db.SaveChangesAsync(ct);
        return new PlanChangeResult("invoice", invoice.Id, invoice.PaymentUrl,
            $"Invoice {invoice.InvoiceNumber} for {invoice.Currency} {invoice.Total:N2} has been issued. Your plan changes once it is paid.");
    }

    private async Task EnsureFitsAsync(Guid tenantId, PlanPrice target, CancellationToken ct)
    {
        var used = await usage.GetLiveAsync(tenantId, ct);
        foreach (var key in SaasUsageService.MeteredKeys.Where(k => k != EntitlementKeys.MaxSessionMinutesPerMonth))
        {
            var limit = target.Plan.Entitlements.FirstOrDefault(x => x.Key == key)?.Value ?? EntitlementKeys.Unlimited;
            var current = used.For(key) ?? 0;
            if (!SaasRules.WithinLimit(limit, current))
                throw new DomainValidationException(
                    $"You currently use {current} {EntitlementKeys.Find(key)?.Unit} but {target.Plan.Name} includes {limit}. Reduce usage before downgrading.");
        }
    }

    // ---------------------------------------------------------------- cancel / resume / trial

    public async Task CancelAsync(Guid tenantId, bool immediate, string actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var sub = await RequireSubscriptionAsync(db, tenantId, ct);
        if (sub.Status is SaasSubscriptionStatus.Cancelled or SaasSubscriptionStatus.Expired)
            throw new ConflictException("This subscription has already ended.");
        if (!immediate && sub.CancelAtPeriodEnd) return; // already scheduled: no second event or e-mail
        var now = DateTime.UtcNow;
        if (immediate)
        {
            sub.Status = SaasSubscriptionStatus.Cancelled; sub.CancelledAtUtc = now; sub.GraceEndsAtUtc = null;
            await VoidOpenInvoicesAsync(db, tenantId, ct);
            await SyncTenantAsync(db, sub, suspend: true, ct);
            AddEvent(db, sub, "cancelled", "Cancelled immediately", actor);
        }
        else
        {
            sub.CancelAtPeriodEnd = true; sub.CancelledAtUtc = now; sub.PendingPlanPriceId = null;
            if (sub.Status == SaasSubscriptionStatus.Active) await VoidOpenInvoicesAsync(db, tenantId, ct);
            AddEvent(db, sub, "cancel_scheduled", "Ends at period end", actor);
        }
        await db.SaveChangesAsync(ct);
        entitlements.Invalidate(tenantId);
        await reminders.NotifyAsync(tenantId, "cancelled", sub.Id.ToString("N") + now.ToString("yyyyMMddHHmm"), ct: ct);
    }

    public async Task<PlanChangeResult> ResumeAsync(Guid tenantId, string actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var sub = await RequireSubscriptionAsync(db, tenantId, ct);
        if (!sub.CancelAtPeriodEnd || sub.Status is not (SaasSubscriptionStatus.Active or SaasSubscriptionStatus.Trialing))
            throw new ConflictException("Only a subscription that is scheduled to end can be resumed. Choose a plan to reactivate.");
        sub.CancelAtPeriodEnd = false; sub.CancelledAtUtc = null;
        AddEvent(db, sub, "resumed", null, actor);
        await db.SaveChangesAsync(ct);
        return new PlanChangeResult("applied", null, null, "Your subscription will continue.");
    }

    public async Task ExtendTrialAsync(Guid tenantId, int days, string actor, CancellationToken ct)
    {
        if (days is < 1 or > 365) throw new DomainValidationException("Extend by 1 to 365 days.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var sub = await RequireSubscriptionAsync(db, tenantId, ct);
        var now = DateTime.UtcNow;
        if (sub.Status is SaasSubscriptionStatus.Active or SaasSubscriptionStatus.PastDue && sub.CurrentPeriodEndUtc is not null)
            throw new ConflictException("Only a trial or an ended subscription can be given a trial extension.");
        var from = sub.Status == SaasSubscriptionStatus.Trialing && sub.TrialEndsAtUtc > now ? sub.TrialEndsAtUtc!.Value : now;
        sub.TrialEndsAtUtc = from.AddDays(days);
        sub.Status = SaasSubscriptionStatus.Trialing; sub.CancelAtPeriodEnd = false;
        var reactivated = await SyncTenantAsync(db, sub, suspend: false, ct);
        AddEvent(db, sub, "trial_extended", $"+{days} days, ends {sub.TrialEndsAtUtc:yyyy-MM-dd}", actor);
        await db.SaveChangesAsync(ct);
        entitlements.Invalidate(tenantId);
        if (reactivated) await reminders.NotifyAsync(tenantId, "reactivated", Guid.NewGuid().ToString("N"), ct: ct);
    }

    // ---------------------------------------------------------------- payments

    /// <summary>
    /// Settles an invoice and moves the subscription onto the paid plan and period. Idempotent: the Open -> Paid flip
    /// is a single conditional UPDATE, so concurrent or replayed callbacks cannot double-apply. Returns false if the
    /// invoice was already paid.
    /// </summary>
    public async Task<bool> ApplyPaymentAsync(Guid invoiceId, string gatewayName, string? paymentRef, decimal? amount, string actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var invoice = await db.PlatformInvoices.SingleOrDefaultAsync(x => x.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice", invoiceId);
        if (invoice.Status == PlatformInvoiceStatus.Void) throw new ConflictException("This invoice was cancelled and can no longer be paid.");
        if (amount is { } paid && paid + 0.01m < invoice.Total)
            throw new DomainValidationException($"Payment of {paid:N2} is less than the invoice total {invoice.Total:N2}; partial payments are not supported.");

        var now = DateTime.UtcNow;
        var updated = await db.PlatformInvoices.Where(x => x.Id == invoiceId && x.Status == PlatformInvoiceStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PlatformInvoiceStatus.Paid).SetProperty(x => x.PaidAtUtc, now), ct);
        if (updated == 0) return false;
        await db.Entry(invoice).ReloadAsync(ct);

        db.PlatformPayments.Add(new PlatformPayment
        {
            InvoiceId = invoice.Id, TenantId = invoice.TenantId, Gateway = gatewayName, GatewayPaymentRef = paymentRef,
            Amount = invoice.Total, Currency = invoice.Currency, Status = PlatformPaymentStatus.Succeeded,
        });

        var sub = await db.TenantSubscriptions.Include(x => x.Plan).SingleAsync(x => x.TenantId == invoice.TenantId, ct);
        var price = invoice.TargetPlanPriceId is { } targetId
            ? await db.PlanPrices.Include(x => x.Plan).SingleAsync(x => x.Id == targetId, ct)
            : sub.PlanPriceId is { } currentId ? await db.PlanPrices.Include(x => x.Plan).SingleAsync(x => x.Id == currentId, ct) : null;
        if (price is null) { await db.SaveChangesAsync(ct); return true; }

        var previous = sub.Status;
        var planChanged = sub.PlanId != price.PlanId;
        var renewal = sub.CurrentPeriodEndUtc is { } oldEnd && invoice.PeriodStartUtc == oldEnd;
        var start = renewal ? invoice.PeriodStartUtc : now;
        var end = SaasRules.AddInterval(start, price.BillingInterval);
        if (end <= now) { start = now; end = SaasRules.AddInterval(now, price.BillingInterval); }

        sub.PlanId = price.PlanId; sub.PlanPriceId = price.Id; sub.Status = SaasSubscriptionStatus.Active;
        sub.CurrentPeriodStartUtc = start; sub.CurrentPeriodEndUtc = end; sub.TrialEndsAtUtc = null;
        sub.GraceEndsAtUtc = null; sub.CancelAtPeriodEnd = false; sub.CancelledAtUtc = null; sub.PendingPlanPriceId = null;
        var reactivated = await SyncTenantAsync(db, sub, suspend: false, ct);

        AddEvent(db, sub, "payment_received", $"{invoice.InvoiceNumber} via {gatewayName}", actor);
        if (previous is SaasSubscriptionStatus.Trialing or SaasSubscriptionStatus.Expired) AddEvent(db, sub, "converted", price.Plan.Code, actor);
        if (planChanged) AddEvent(db, sub, "plan_changed", price.Plan.Code, actor);
        await db.SaveChangesAsync(ct);
        entitlements.Invalidate(invoice.TenantId);

        var extra = new Dictionary<string, string> { ["plan_name"] = price.Plan.Name };
        await reminders.NotifyAsync(invoice.TenantId, "payment_success", invoice.Id.ToString("N"), invoice.Id, extra, ct);
        if (reactivated) await reminders.NotifyAsync(invoice.TenantId, "reactivated", invoice.Id.ToString("N"), invoice.Id, extra, ct);
        else if (planChanged && !renewal) await reminders.NotifyAsync(invoice.TenantId, "plan_changed", invoice.Id.ToString("N"), invoice.Id, extra, ct);
        return true;
    }

    public async Task RecordPaymentFailureAsync(Guid invoiceId, string gatewayName, string? paymentRef, string? reason, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var invoice = await db.PlatformInvoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invoiceId, ct);
        if (invoice is null || invoice.Status != PlatformInvoiceStatus.Open) return;
        var payment = new PlatformPayment
        {
            InvoiceId = invoice.Id, TenantId = invoice.TenantId, Gateway = gatewayName, GatewayPaymentRef = paymentRef,
            Amount = invoice.Total, Currency = invoice.Currency, Status = PlatformPaymentStatus.Failed,
            FailureReason = reason is { Length: > 480 } ? reason[..480] : reason,
        };
        db.PlatformPayments.Add(payment);
        var sub = await db.TenantSubscriptions.SingleOrDefaultAsync(x => x.TenantId == invoice.TenantId, ct);
        if (sub is not null) AddEvent(db, sub, "payment_failed", $"{invoice.InvoiceNumber}: {reason}", "system");
        await db.SaveChangesAsync(ct);
        await reminders.NotifyAsync(invoice.TenantId, "payment_failed", payment.Id.ToString("N"), invoice.Id, ct: ct);
    }

    public async Task VoidInvoiceAsync(Guid invoiceId, string actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var invoice = await db.PlatformInvoices.SingleOrDefaultAsync(x => x.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice", invoiceId);
        if (invoice.Status != PlatformInvoiceStatus.Open) throw new ConflictException("Only open invoices can be voided.");
        invoice.Status = PlatformInvoiceStatus.Void;
        var sub = await db.TenantSubscriptions.SingleOrDefaultAsync(x => x.TenantId == invoice.TenantId, ct);
        if (sub is not null) AddEvent(db, sub, "invoice_voided", invoice.InvoiceNumber, actor);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Creates (or recreates) the online payment link for an open invoice.</summary>
    public async Task<string?> EnsurePaymentLinkAsync(Guid invoiceId, Guid? onlyForTenant, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var invoice = await db.PlatformInvoices.SingleOrDefaultAsync(x => x.Id == invoiceId && (onlyForTenant == null || x.TenantId == onlyForTenant), ct)
            ?? throw new NotFoundException("Invoice", invoiceId);
        if (invoice.Status != PlatformInvoiceStatus.Open) throw new ConflictException("Only open invoices can be paid.");
        if (!string.IsNullOrWhiteSpace(invoice.PaymentUrl)) return invoice.PaymentUrl;
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == invoice.TenantId, ct);
        await AttachPaymentLinkAsync(db, invoice, tenant, ct);
        await db.SaveChangesAsync(ct);
        return invoice.PaymentUrl;
    }

    // ---------------------------------------------------------------- billing cycle

    /// <summary>One pass of the billing cycle: trial expiry, renewal invoices, past-due marking, suspensions.</summary>
    public async Task<int> RunLifecycleAsync(DateTime now, CancellationToken ct)
    {
        List<Guid> due;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var lead = now.AddDays(Options.InvoiceLeadDays);
            due = await db.TenantSubscriptions.AsNoTracking().Where(x =>
                    (x.Status == SaasSubscriptionStatus.Trialing && x.TrialEndsAtUtc <= now)
                    || (x.Status == SaasSubscriptionStatus.Active && x.CurrentPeriodEndUtc != null && x.CurrentPeriodEndUtc <= lead)
                    || (x.Status == SaasSubscriptionStatus.PastDue && x.GraceEndsAtUtc <= now))
                .Select(x => x.TenantId).ToListAsync(ct);
        }
        var handled = 0;
        foreach (var tenantId in due)
        {
            try { if (await ProcessAsync(tenantId, now, ct)) handled++; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Billing cycle failed for tenant {TenantId}; other tenants continue.", tenantId);
            }
        }
        return handled;
    }

    private async Task<bool> ProcessAsync(Guid tenantId, DateTime now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var sub = await db.TenantSubscriptions.Include(x => x.Plan).Include(x => x.PlanPrice).SingleAsync(x => x.TenantId == tenantId, ct);
        var openRenewal = sub.CurrentPeriodEndUtc is { } end
            && await db.PlatformInvoices.AnyAsync(x => x.TenantId == tenantId && x.Status == PlatformInvoiceStatus.Open && x.PeriodStartUtc == end, ct);
        var action = SaasRules.EvaluateLifecycle(sub, now, Options.InvoiceLeadDays, openRenewal);
        if (action == LifecycleAction.None) return false;

        switch (action)
        {
            case LifecycleAction.ExpireTrial:
                sub.Status = SaasSubscriptionStatus.Expired;
                await SyncTenantAsync(db, sub, suspend: true, ct);
                AddEvent(db, sub, "trial_expired", null, "system");
                await db.SaveChangesAsync(ct);
                entitlements.Invalidate(tenantId);
                await reminders.NotifyAsync(tenantId, "trial_expired", sub.TrialEndsAtUtc!.Value.ToString("yyyyMMdd"), ct: ct);
                break;

            case LifecycleAction.GenerateRenewalInvoice:
                await IssueRenewalInvoiceAsync(db, sub, now, ct);
                break;

            case LifecycleAction.MarkPastDue:
                sub.Status = SaasSubscriptionStatus.PastDue;
                sub.GraceEndsAtUtc = (sub.CurrentPeriodEndUtc!.Value > now ? sub.CurrentPeriodEndUtc.Value : now).AddDays(Options.GraceDays);
                AddEvent(db, sub, "past_due", $"Grace until {sub.GraceEndsAtUtc:yyyy-MM-dd}", "system");
                await db.SaveChangesAsync(ct);
                entitlements.Invalidate(tenantId);
                await reminders.NotifyAsync(tenantId, "past_due_started", sub.CurrentPeriodEndUtc!.Value.ToString("yyyyMMdd"), ct: ct);
                break;

            case LifecycleAction.CancelAtPeriodEnd:
                sub.Status = SaasSubscriptionStatus.Cancelled; sub.CancelledAtUtc ??= now;
                await SyncTenantAsync(db, sub, suspend: true, ct);
                AddEvent(db, sub, "cancelled", "Period ended", "system");
                await db.SaveChangesAsync(ct);
                entitlements.Invalidate(tenantId);
                break;

            case LifecycleAction.SuspendAfterGrace:
                sub.Status = SaasSubscriptionStatus.Suspended;
                await SyncTenantAsync(db, sub, suspend: true, ct);
                AddEvent(db, sub, "suspended", "Grace period ended without payment", "system");
                await db.SaveChangesAsync(ct);
                entitlements.Invalidate(tenantId);
                await reminders.NotifyAsync(tenantId, "suspended", sub.GraceEndsAtUtc!.Value.ToString("yyyyMMdd"), ct: ct);
                break;
        }
        return true;
    }

    private async Task IssueRenewalInvoiceAsync(MasterDbContext db, TenantSubscription sub, DateTime now, CancellationToken ct)
    {
        var price = sub.PendingPlanPriceId is { } pendingId
            ? await db.PlanPrices.Include(x => x.Plan).SingleAsync(x => x.Id == pendingId, ct)
            : sub.PlanPriceId is { } currentId ? await db.PlanPrices.Include(x => x.Plan).SingleAsync(x => x.Id == currentId, ct) : null;
        if (price is null || price.Amount <= 0) return;
        var tenant = await db.Tenants.SingleAsync(x => x.Id == sub.TenantId, ct);
        var start = sub.CurrentPeriodEndUtc!.Value;
        var interval = price.BillingInterval == BillingInterval.Year ? "yearly" : "monthly";
        var lines = new List<PlatformInvoiceLine>
        {
            new() { Description = $"{price.Plan.Name} plan ({interval}) renewal", Quantity = 1, UnitAmount = price.Amount, Amount = price.Amount },
        };
        var invoice = await BuildInvoiceAsync(db, tenant, sub, lines, start, SaasRules.AddInterval(start, price.BillingInterval), start, price, ct);
        AddEvent(db, sub, "invoice_issued", $"{invoice.InvoiceNumber} renewal {invoice.Currency} {invoice.Total}", "system");
        await db.SaveChangesAsync(ct);
        await AttachPaymentLinkAsync(db, invoice, tenant, ct);
        await db.SaveChangesAsync(ct);
        await reminders.NotifyAsync(sub.TenantId, "renewal_upcoming", invoice.Id.ToString("N"), invoice.Id,
            new Dictionary<string, string> { ["plan_name"] = price.Plan.Name }, ct);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<TenantSubscription> RequireSubscriptionAsync(MasterDbContext db, Guid tenantId, CancellationToken ct) =>
        await db.TenantSubscriptions.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct)
        ?? throw new NotFoundException("This tenant has no subscription yet.");

    private static void AddEvent(MasterDbContext db, TenantSubscription sub, string type, string? detail, string actor) =>
        db.TenantSubscriptionEvents.Add(new TenantSubscriptionEvent
        {
            TenantId = sub.TenantId, SubscriptionId = sub.Id, EventType = type,
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail, Actor = actor,
        });

    /// <summary>
    /// Keeps Tenant.Status in step. Billing may only reactivate a tenant it suspended itself; a tenant an operator
    /// suspended by hand stays suspended even after payment. Returns true when the tenant was reactivated.
    /// </summary>
    private static async Task<bool> SyncTenantAsync(MasterDbContext db, TenantSubscription sub, bool suspend, CancellationToken ct)
    {
        var tenant = await db.Tenants.SingleAsync(x => x.Id == sub.TenantId, ct);
        if (suspend)
        {
            if (tenant.Status == TenantStatus.Active)
            {
                tenant.Status = TenantStatus.Suspended;
                sub.SuspendedByBilling = true;
            }
            return false;
        }
        if (sub.SuspendedByBilling && tenant.Status == TenantStatus.Suspended)
        {
            tenant.Status = TenantStatus.Active;
            sub.SuspendedByBilling = false;
            return true;
        }
        sub.SuspendedByBilling = false;
        return false;
    }

    private async Task VoidOpenInvoicesAsync(MasterDbContext db, Guid tenantId, CancellationToken ct) =>
        await db.PlatformInvoices.Where(x => x.TenantId == tenantId && x.Status == PlatformInvoiceStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PlatformInvoiceStatus.Void), ct);

    private async Task<PlatformInvoice> BuildInvoiceAsync(MasterDbContext db, Tenant tenant, TenantSubscription sub,
        List<PlatformInvoiceLine> lines, DateTime periodStart, DateTime periodEnd, DateTime due, PlanPrice target, CancellationToken ct)
    {
        var subtotal = SaasRules.Round(lines.Sum(x => x.Amount));
        var tax = SaasRules.TaxFor(Math.Max(0m, subtotal), Options.TaxRatePercent);
        var seq = await db.Database.SqlQueryRaw<long>("SELECT nextval('platform_invoice_number_seq') AS \"Value\"").SingleAsync(ct);
        var invoice = new PlatformInvoice
        {
            InvoiceNumber = $"{Options.InvoicePrefix}-{DateTime.UtcNow:yyyy}-{seq:D6}",
            TenantId = tenant.Id, SubscriptionId = sub.Id, PeriodStartUtc = periodStart, PeriodEndUtc = periodEnd,
            Subtotal = Math.Max(0m, subtotal), TaxRatePercent = Options.TaxRatePercent, TaxAmount = tax,
            Total = Math.Max(0m, subtotal) + tax, Currency = target.Currency, DueAtUtc = due, TargetPlanPriceId = target.Id,
            Lines = lines,
        };
        db.PlatformInvoices.Add(invoice);
        return invoice;
    }

    private async Task AttachPaymentLinkAsync(MasterDbContext db, PlatformInvoice invoice, Tenant tenant, CancellationToken ct)
    {
        try
        {
            var email = await db.TenantBillingProfiles.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.BillingEmail).FirstOrDefaultAsync(ct);
            var link = await gateway.CreatePaymentLinkAsync(invoice, tenant.Name, email, ct);
            if (link is null) return;
            invoice.GatewayOrderRef = link.Reference;
            invoice.PaymentUrl = link.Url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Payment link for invoice {Invoice} could not be created; it can be requested again.", invoice.InvoiceNumber);
        }
    }
}
