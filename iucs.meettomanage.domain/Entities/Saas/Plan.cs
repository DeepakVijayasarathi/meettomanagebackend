using System.ComponentModel.DataAnnotations;

namespace iucs.meettomanage.domain.Entities.Saas;

/// <summary>A sellable package. Prices and entitlements are data so they change without a deployment.</summary>
public class Plan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(50)] public string Code { get; set; } = null!;
    [MaxLength(150)] public string Name { get; set; } = null!;
    [MaxLength(1000)] public string? Description { get; set; }
    /// <summary>Hidden plans (legacy, enterprise) are assigned by an operator and never shown to buyers.</summary>
    public bool IsPublic { get; set; } = true;
    public bool IsActive { get; set; } = true;
    /// <summary>Days of free use a tenant gets when it starts on this plan; 0 means no trial.</summary>
    public int TrialDays { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<PlanPrice> Prices { get; set; } = [];
    public List<PlanEntitlement> Entitlements { get; set; } = [];
}

/// <summary>
/// One price point of a plan. A subscription points at a specific price row, so changing a plan's
/// price later (a new row) never alters what existing subscribers pay (grandfathering).
/// </summary>
public class PlanPrice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public Plan Plan { get; set; } = null!;
    public BillingInterval BillingInterval { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "INR";
    public decimal Amount { get; set; }
    /// <summary>Plan id at the payment gateway, when the gateway manages the schedule.</summary>
    [MaxLength(128)] public string? GatewayPlanRef { get; set; }
    /// <summary>Inactive prices stay valid for existing subscribers but cannot be chosen by new ones.</summary>
    public bool IsActive { get; set; } = true;
    public DateTime EffectiveFromUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A feature flag (Bool, 0/1) or numeric limit (Number, -1 = unlimited) granted by a plan.</summary>
public class PlanEntitlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public Plan Plan { get; set; } = null!;
    [MaxLength(64)] public string Key { get; set; } = null!;
    public EntitlementValueType ValueType { get; set; }
    public long Value { get; set; }
}
