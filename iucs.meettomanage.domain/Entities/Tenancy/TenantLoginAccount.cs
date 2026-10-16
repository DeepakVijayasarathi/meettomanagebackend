namespace iucs.meettomanage.domain.Entities.Tenancy;

/// <summary>One global user ID has exactly one tenant, regardless of its login aliases.</summary>
public sealed class TenantLoginAccount
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    [System.Text.Json.Serialization.JsonIgnore]
    public string PinHash { get; set; } = "";
    public bool IsDeleted { get; set; }
}
