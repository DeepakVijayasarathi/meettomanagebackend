namespace iucs.meettomanage.domain.Entities.Tenancy;

/// <summary>
/// Globally unique normalized login alias. Email and mobile aliases share a user
/// and tenant; the browser never needs to supply a tenant code to log in.
/// </summary>
public sealed class TenantLoginIdentifier
{
    public string Identifier { get; set; } = null!;
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public TenantLoginAccount Account { get; set; } = null!;
}
