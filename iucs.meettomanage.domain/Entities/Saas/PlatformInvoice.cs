using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Tenancy;

namespace iucs.meettomanage.domain.Entities.Saas;

/// <summary>An invoice the platform issues to a tenant for its subscription.</summary>
public class PlatformInvoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public string InvoiceNumber { get; set; } = null!;
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public Guid? SubscriptionId { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxRatePercent { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "INR";
    public PlatformInvoiceStatus Status { get; set; } = PlatformInvoiceStatus.Open;
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime DueAtUtc { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    /// <summary>The plan price a successful payment moves the subscription onto (renewal, upgrade or new purchase).</summary>
    public Guid? TargetPlanPriceId { get; set; }
    /// <summary>Gateway-side id of the payment link/order created for this invoice.</summary>
    [MaxLength(128)] public string? GatewayOrderRef { get; set; }
    [MaxLength(1000)] public string? PaymentUrl { get; set; }

    public List<PlatformInvoiceLine> Lines { get; set; } = [];
    public List<PlatformPayment> Payments { get; set; } = [];
}

public class PlatformInvoiceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public PlatformInvoice Invoice { get; set; } = null!;
    [MaxLength(300)] public string Description { get; set; } = null!;
    public int Quantity { get; set; } = 1;
    public decimal UnitAmount { get; set; }
    public decimal Amount { get; set; }
}

public class PlatformPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public PlatformInvoice Invoice { get; set; } = null!;
    public Guid TenantId { get; set; }
    /// <summary>"manual" or the gateway name, e.g. "razorpay".</summary>
    [MaxLength(40)] public string Gateway { get; set; } = null!;
    [MaxLength(128)] public string? GatewayPaymentRef { get; set; }
    public decimal Amount { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "INR";
    public PlatformPaymentStatus Status { get; set; }
    [MaxLength(500)] public string? FailureReason { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Idempotency record: a gateway event id is processed at most once.</summary>
public class PlatformWebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public string Provider { get; set; } = null!;
    [MaxLength(128)] public string ExternalEventId { get; set; } = null!;
    [MaxLength(80)] public string EventType { get; set; } = null!;
    public string Payload { get; set; } = "";
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
}
