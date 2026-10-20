using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using iucs.meettomanage.api.Saas;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace iucs.meettomanage.api.Controllers;

public sealed class SignupRequest
{
    [Required, MaxLength(200)] public string OrganizationName { get; set; } = "";
    [Required, RegularExpression("^[a-z][a-z0-9-]{2,49}$")] public string Code { get; set; } = "";
    [Required, EmailAddress, MaxLength(256)] public string AdminEmail { get; set; } = "";
    [Required, RegularExpression(@"^\d{4}$")] public string AdminPin { get; set; } = "";
    [MaxLength(30)] public string? Phone { get; set; }
    public bool AcceptTerms { get; set; }
}

/// <summary>Anonymous endpoints: the public plan list, tenant-code check and self-service signup.</summary>
[ApiController, Route("api/platform/public"), AllowAnonymous]
public sealed class PublicSaasController(MasterDbContext master, TenantProvisioner provisioner, SaasSubscriptionService subscriptions,
    IOptions<SaasOptions> options) : ControllerBase
{
    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct)
    {
        var plans = await master.Plans.AsNoTracking().Include(x => x.Prices).Include(x => x.Entitlements)
            .Where(x => x.IsPublic && x.IsActive).OrderBy(x => x.SortOrder).ToListAsync(ct);
        return Ok(plans.Select(p => new
        {
            p.Id, p.Code, p.Name, p.Description, p.TrialDays,
            prices = p.Prices.Where(x => x.IsActive).OrderBy(x => x.BillingInterval)
                .Select(x => new { x.Id, billingInterval = x.BillingInterval.ToString(), x.Currency, x.Amount }),
            entitlements = p.Entitlements.Select(x => new { x.Key, x.Value }),
        }));
    }

    [HttpGet("signup/settings")]
    public IActionResult Settings() => Ok(new { enabled = options.Value.SignupEnabled, trialDays = options.Value.DefaultTrialDays, platformName = options.Value.PlatformName });

    [HttpGet("signup/check-code"), EnableRateLimiting("login")]
    public async Task<IActionResult> CheckCode([FromQuery] string code, CancellationToken ct)
    {
        code = (code ?? "").Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[a-z][a-z0-9-]{2,49}$"))
            return Ok(new { available = false, reason = "Use 3-50 lowercase letters, digits or hyphens, starting with a letter." });
        if (options.Value.ReservedTenantCodes.Contains(code)) return Ok(new { available = false, reason = "That address is reserved." });
        var taken = await master.Tenants.AnyAsync(x => x.Code == code, ct);
        return Ok(new { available = !taken, reason = taken ? "That address is already taken." : null });
    }

    [HttpPost("signup"), EnableRateLimiting("login")]
    public async Task<IActionResult> Signup(SignupRequest request, CancellationToken ct)
    {
        var opt = options.Value;
        if (!opt.SignupEnabled) throw new ForbiddenException("Self-service signup is not enabled.");
        if (!request.AcceptTerms) throw new DomainValidationException("Please accept the terms to continue.");
        var code = request.Code.Trim().ToLowerInvariant();
        var email = request.AdminEmail.Trim().ToLowerInvariant();
        if (opt.ReservedTenantCodes.Contains(code)) throw new DomainValidationException("That address is reserved.");
        if (await master.Tenants.AnyAsync(x => x.Code == code, ct)) throw new ConflictException("That address is already taken.");
        // One trial per e-mail: either a billing profile or a login already exists for it.
        if (await master.TenantBillingProfiles.AnyAsync(x => x.BillingEmail == email, ct)
            || await master.LoginIdentifiers.AnyAsync(x => x.Identifier == TenantResolver.NormalizeIdentifier(email), ct))
            throw new ConflictException("An account already exists for this e-mail address. Sign in instead.");

        var tenant = await provisioner.CreateAsync(new CreateTenantRequest
        {
            Code = code, Name = request.OrganizationName.Trim(), AdminEmail = email, AdminPin = request.AdminPin,
        }, ct);
        await subscriptions.EnsureSubscriptionAsync(tenant.Id, "trial", "signup", email, request.OrganizationName, ct);
        return Ok(new { tenantCode = tenant.Code, loginPath = $"/t/{tenant.Code}/login" });
    }
}

/// <summary>Payment-gateway callbacks. Authenticated by the gateway's HMAC signature, never by a bearer token.</summary>
[ApiController, Route("api/platform/billing/webhooks"), AllowAnonymous]
public sealed class PlatformBillingWebhookController(MasterDbContext master, IPlatformPaymentGateway gateway,
    SaasSubscriptionService subscriptions, ILogger<PlatformBillingWebhookController> logger) : ControllerBase
{
    [HttpPost("razorpay")]
    public async Task<IActionResult> Razorpay(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (!gateway.VerifyWebhookSignature(body, Request.Headers["X-Razorpay-Signature"].FirstOrDefault()))
            throw new UnauthorizedException("Invalid webhook signature.");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var eventType = root.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
        var eventId = Request.Headers["X-Razorpay-Event-Id"].FirstOrDefault()
            ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));

        var record = new PlatformWebhookEvent { Provider = "razorpay", ExternalEventId = eventId, EventType = eventType, Payload = body };
        master.PlatformWebhookEvents.Add(record);
        try { await master.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Ok(new { duplicate = true }); // already processed: Razorpay retries must be harmless
        }

        try
        {
            switch (eventType)
            {
                case "payment_link.paid":
                {
                    var link = root.GetProperty("payload").GetProperty("payment_link").GetProperty("entity");
                    var payment = root.GetProperty("payload").GetProperty("payment").GetProperty("entity");
                    var invoice = await FindInvoiceAsync(link.GetProperty("id").GetString(),
                        link.TryGetProperty("reference_id", out var reference) ? reference.GetString() : null, ct);
                    if (invoice is not null)
                        await subscriptions.ApplyPaymentAsync(invoice.Id, "razorpay", payment.GetProperty("id").GetString(),
                            payment.GetProperty("amount").GetInt64() / 100m, "webhook", ct);
                    else logger.LogWarning("Razorpay payment_link.paid for an unknown payment link.");
                    break;
                }
                case "payment.failed":
                {
                    var payment = root.GetProperty("payload").GetProperty("payment").GetProperty("entity");
                    string? number = null;
                    if (payment.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Object
                        && notes.TryGetProperty("invoiceNumber", out var n)) number = n.GetString();
                    var invoice = await FindInvoiceAsync(null, number, ct);
                    if (invoice is not null)
                        await subscriptions.RecordPaymentFailureAsync(invoice.Id, "razorpay", payment.GetProperty("id").GetString(),
                            payment.TryGetProperty("error_description", out var d) ? d.GetString() : null, ct);
                    break;
                }
            }
            record.ProcessedAtUtc = DateTime.UtcNow;
            await master.SaveChangesAsync(ct);
        }
        catch
        {
            // Forget the event so the gateway's retry is processed instead of being dropped as a duplicate.
            master.PlatformWebhookEvents.Remove(record);
            await master.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        return Ok();
    }

    private Task<PlatformInvoice?> FindInvoiceAsync(string? gatewayRef, string? invoiceNumber, CancellationToken ct) =>
        master.PlatformInvoices.AsNoTracking().FirstOrDefaultAsync(x =>
            (gatewayRef != null && x.GatewayOrderRef == gatewayRef) || (invoiceNumber != null && x.InvoiceNumber == invoiceNumber), ct);
}
