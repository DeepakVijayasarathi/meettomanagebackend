using iucs.meettomanage.domain.Entities.Tenancy;

namespace iucs.meettomanage.api.Tenancy;

/// <summary>Workers and provisioning always create an explicitly bound business scope.</summary>
public sealed class TenantScopeFactory(IServiceScopeFactory scopes, IConfiguration configuration)
{
    public IServiceScope CreateScope(Tenant tenant)
    {
        var scope = scopes.CreateScope();
        try
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Bind(tenant.Id, tenant.Code,
                TenantResolver.BuildConnectionString(configuration.GetConnectionString("TenantServer")!, tenant.DatabaseName));
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }
}
