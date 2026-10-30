using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace iucs.meettomanage.api.Tenancy;

/// <summary>
/// Permanently removes a tenant: its dedicated database and every control-plane row that belongs to it
/// (subscription, invoices, payments, billing profile, login registry). The platform audit trail is kept.
/// Irreversible; callers must have confirmed with the operator first.
/// </summary>
public sealed class TenantEraser(MasterDbContext master, IConfiguration configuration, TenantDomainResolver domains, ILogger<TenantEraser> logger)
{
    public async Task EraseAsync(Guid id, CancellationToken ct)
    {
        var tenant = await master.Tenants.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
        // Names come from the registry but become SQL identifiers, so re-validate and never touch the registry itself.
        if (!TenantResolver.IsValidDatabaseName(tenant.DatabaseName)
            || string.Equals(tenant.DatabaseName, new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("MasterDb")).Database, StringComparison.Ordinal))
            throw new ConflictException("This tenant's database name is not safe to drop automatically.");

        // Stop serving the tenant before its data goes.
        tenant.Status = TenantStatus.Suspended;
        await master.SaveChangesAsync(ct);

        await DropDatabaseAsync(tenant.DatabaseName, ct);

        await using var tx = await master.Database.BeginTransactionAsync(ct);
        await master.PlatformPayments.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await master.PlatformInvoices.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct); // lines cascade
        await master.SubscriptionReminderLogs.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await master.TenantSubscriptionEvents.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await master.TenantEntitlementOverrides.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await master.TenantUsageSnapshots.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await master.TenantBillingProfiles.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await master.TenantSubscriptions.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await master.LoginIdentifiers.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await master.LoginAccounts.Where(x => x.TenantId == id).ExecuteDeleteAsync(ct);
        await master.Tenants.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);

        domains.Invalidate(tenant.CustomDomain);
        logger.LogWarning("Tenant {TenantId} ({Code}) and database {Database} were permanently deleted.", id, tenant.Code, tenant.DatabaseName);
    }

    private async Task DropDatabaseAsync(string databaseName, CancellationToken ct)
    {
        var provisionConnection = configuration.GetConnectionString("TenantProvisioning");
        var server = string.IsNullOrWhiteSpace(provisionConnection) ? configuration.GetConnectionString("TenantServer")! : provisionConnection;
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ConnectionString);
        await connection.OpenAsync(ct);
        var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        // FORCE disconnects anyone still attached (PostgreSQL 13+); a database that is already gone is fine.
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {quoted} WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync(ct);
        NpgsqlConnection.ClearAllPools();
    }
}
