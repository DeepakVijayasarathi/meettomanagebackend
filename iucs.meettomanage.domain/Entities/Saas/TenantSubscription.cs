using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Tenancy;

namespace iucs.meettomanage.domain.Entities.Saas;

/// <summary>
/// The tenant's agreement with the platform; exactly one row per tenant. A null
/// <see cref="CurrentPeriodEndUtc"/> means the plan never renews (legacy/comped tenants).
/// </summary>
public class TenantSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public Guid PlanId { get; set; }
    public Plan Plan { get; set; } = null!;
    /// <summary>Null while trialing or on a plan with no price (legacy).</summary>
    public Guid? PlanPriceId { get; set; }
    public PlanPrice? PlanPrice { get; set; }
    public SaasSubscriptionStatus Status { get; set; }
    public DateTime? TrialEndsAtUtc { get; set; }
    public DateTime? CurrentPeriodStartUtc { get; set; }
    public DateTime? CurrentPeriodEndUtc { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? GraceEndsAtUtc { get; set; }
    /// <summary>True when the billing cycle (not an operator) suspended the tenant, so payment may reactivate it.</summary>
    public bool SuspendedByBilling { get; set; }
    /// <summary>A downgrade chosen mid-period; applied at the next renewal.</summary>
    public Guid? PendingPlanPriceId { get; set; }
    [MaxLength(128)] public string? GatewayCustomerRef { get; set; }
    [MaxLength(128)] public string? GatewaySubscriptionRef { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Append-only audit trail of everything that happened to a subscription.</summary>
public class TenantSubscriptionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SubscriptionId { get; set; }
    [MaxLength(64)] public string EventType { get; set; } = null!;
    [MaxLength(1000)] public string? Detail { get; set; }
    /// <summary>"system", "platform:email" or "tenant".</summary>
    [MaxLength(256)] public string Actor { get; set; } = "system";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A per-tenant exception to its plan: a comped feature or a custom limit.</summary>
public class TenantEntitlementOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    [MaxLength(64)] public string Key { get; set; } = null!;
    public EntitlementValueType ValueType { get; set; }
    public long Value { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
