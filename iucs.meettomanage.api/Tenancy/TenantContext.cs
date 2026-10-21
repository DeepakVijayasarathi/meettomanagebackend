namespace iucs.meettomanage.api.Tenancy;

/// <summary>Immutable after binding, to prevent changing databases within a request/job.</summary>
public sealed class TenantContext
{
    public const string TenantIdClaim = "tenant_id";
    public const string TenantCodeClaim = "tenant_code";

    public Guid? TenantId { get; private set; }
    public string? TenantCode { get; private set; }
    internal string? ConnectionString { get; private set; }
    /// <summary>Whether the bound tenant allows the mobile parent app (see AppAccessPolicy).</summary>
    public bool AppAccessGranted { get; private set; } = true;

    internal void Bind(Guid id, string code, string connectionString, bool appAccessGranted = true)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A complete tenant binding is required.");
        if (TenantId is not null)
        {
            if (TenantId != id || TenantCode != code || ConnectionString != connectionString)
                throw new InvalidOperationException("The tenant cannot change within an existing scope.");
            return;
        }
        AppAccessGranted = appAccessGranted;
        TenantId = id;
        TenantCode = code;
        ConnectionString = connectionString;
    }

    public string RequireConnectionString() => ConnectionString
        ?? throw new InvalidOperationException("A tenant must be resolved before accessing business data.");
}
