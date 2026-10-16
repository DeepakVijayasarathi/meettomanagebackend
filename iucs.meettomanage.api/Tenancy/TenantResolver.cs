using System.Text.RegularExpressions;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace iucs.meettomanage.api.Tenancy;

public sealed class TenantResolver(MasterDbContext master, TenantContext current, IConfiguration configuration)
{
    public static string NormalizeIdentifier(string identifier)
    {
        var value = identifier.Trim();
        if (value.Contains('@')) return "email:" + value.ToLowerInvariant();
        var phone = ParentLogin.NormalizePhone(value)
            ?? throw new UnauthorizedException("Invalid email/mobile number or PIN.");
        return "phone:" + phone;
    }

    public async Task ResolveLoginAsync(string identifier, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeIdentifier(identifier);
        var tenant = await master.LoginIdentifiers.AsNoTracking()
            .Where(x => x.Identifier == normalized).Select(x => x.Account.Tenant)
            .SingleOrDefaultAsync(cancellationToken);
        BindActive(tenant);
    }

    public async Task ResolveTokenAsync(Guid tenantId, string tenantCode, CancellationToken cancellationToken = default)
    {
        var tenant = await master.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == tenantId && x.Code == tenantCode, cancellationToken);
        BindActive(tenant);
    }

    public async Task ResolveCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var tenant = await master.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Code == code, cancellationToken);
        BindActive(tenant);
    }

    internal async Task BindKnownTokenAsync(Guid id, string code)
    {
        var tenant = await master.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Code == code);
        if (tenant is null || tenant.Status is not (TenantStatus.Active or TenantStatus.Suspended))
            throw new UnauthorizedException("The tenant is unavailable.");
        // Used to construct SignalR invocation scopes, including disconnect cleanup.
        // TenantHubFilter separately demands Active before invoking any client method.
        BindForProvisioning(tenant);
    }

    public void BindForProvisioning(Tenant tenant)
    {
        current.Bind(tenant.Id, tenant.Code, BuildConnectionString(
            configuration.GetConnectionString("TenantServer")!, tenant.DatabaseName));
    }

    private void BindActive(Tenant? tenant)
    {
        if (tenant is null || tenant.Status != TenantStatus.Active)
            throw new UnauthorizedException("The account or tenant is unavailable.");
        current.Bind(tenant.Id, tenant.Code, BuildConnectionString(
            configuration.GetConnectionString("TenantServer")
                ?? throw new InvalidOperationException("ConnectionStrings:TenantServer is required."),
            tenant.DatabaseName));
    }

    public static string BuildConnectionString(string serverConnectionString, string databaseName)
    {
        // Names are generated on the server. Never accept a connection string or database
        // name from a header, query parameter, JWT, or browser provisioning form.
        if (!Regex.IsMatch(databaseName, @"^mt_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("The tenant registry contains an invalid database name.");
        return new NpgsqlConnectionStringBuilder(serverConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false,
            PersistSecurityInfo = false,
        }.ConnectionString;
    }
}
