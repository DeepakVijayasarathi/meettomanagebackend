using System.ComponentModel.DataAnnotations;

namespace iucs.meettomanage.domain.Entities.Tenancy;

public enum TenantStatus { Provisioning, Active, Suspended, Failed }

/// <summary>Control-plane record. DatabaseName is resolved only on the server.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(50)] public string Code { get; set; } = null!;
    [MaxLength(200)] public string Name { get; set; } = null!;
    /// <summary>Optional verified hostname used in addition to /t/{code}, without scheme or path.</summary>
    [MaxLength(253)] public string? CustomDomain { get; set; }
    public DateTime? CustomDomainVerifiedAtUtc { get; set; }
    [MaxLength(63)] public string DatabaseName { get; set; } = null!;
    public TenantStatus Status { get; set; } = TenantStatus.Provisioning;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProvisionedAtUtc { get; set; }
    /// <summary>Whether this tenant's families may use the mobile parent app. Set when the tenant is created; changed by the operator.</summary>
    public bool AppAccessGranted { get; set; } = true;
}
