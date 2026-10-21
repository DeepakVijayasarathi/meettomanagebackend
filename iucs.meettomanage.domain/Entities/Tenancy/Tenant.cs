using System.ComponentModel.DataAnnotations;

namespace iucs.meettomanage.domain.Entities.Tenancy;

public enum TenantStatus { Provisioning, Active, Suspended, Failed }

/// <summary>Control-plane record. DatabaseName is resolved only on the server.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(50)] public string Code { get; set; } = null!;
    [MaxLength(200)] public string Name { get; set; } = null!;
    [MaxLength(63)] public string DatabaseName { get; set; } = null!;
    public TenantStatus Status { get; set; } = TenantStatus.Provisioning;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProvisionedAtUtc { get; set; }
    /// <summary>Whether this tenant's families may use the mobile parent app. Set when the tenant is created; changed by the operator.</summary>
    public bool AppAccessGranted { get; set; } = true;
}
