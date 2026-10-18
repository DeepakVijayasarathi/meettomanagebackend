using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.api.Data;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace iucs.meettomanage.api.Tenancy;

public sealed class CreateTenantRequest
{
    [Required, RegularExpression("^[a-z][a-z0-9-]{2,49}$")] public string Code { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    /// <summary>
    /// Optional. Name of a PostgreSQL database on the tenant server to use for this client.
    /// If it already exists it is kept as-is and only its missing migrations are applied;
    /// if it does not exist it is created. Omit to get a generated mt_* database.
    /// </summary>
    [RegularExpression("^[a-z_][a-z0-9_]{0,62}$")] public string? DatabaseName { get; set; }
    // Required when a new database is created. Ignored for an existing database, so an
    // existing administrator's PIN is never overwritten.
    [EmailAddress, MaxLength(256)] public string? AdminEmail { get; set; }
    [RegularExpression(@"^\d{4}$")] public string? AdminPin { get; set; }
}

public sealed class TenantProvisioner(IServiceProvider services, IDbContextFactory<MasterDbContext> masterFactory,
    IConfiguration configuration, ILogger<TenantProvisioner> logger)
{
    public async Task<Tenant> CreateAsync(CreateTenantRequest request, CancellationToken ct)
    {
        await using var master = await masterFactory.CreateDbContextAsync(ct);
        var databaseName = string.IsNullOrWhiteSpace(request.DatabaseName)
            ? "mt_" + Guid.NewGuid().ToString("N") : request.DatabaseName.Trim();
        if (!TenantResolver.IsValidDatabaseName(databaseName)
            || string.Equals(databaseName, MasterDatabaseName(), StringComparison.Ordinal))
            throw new DomainValidationException("Use a lowercase database name of letters, digits and underscores that is not a system or registry database.");
        var tenant = new Tenant { Code = request.Code, Name = request.Name.Trim(), DatabaseName = databaseName };
        master.Tenants.Add(tenant);
        try { await master.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("The tenant code or database name is already registered."); }
        return await ProvisionAsync(tenant.Id, request, ct);
    }

    private string? MasterDatabaseName() =>
        new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("MasterDb")).Database;

    public async Task<Tenant> ProvisionAsync(Guid id, CreateTenantRequest request, CancellationToken ct)
    {
        await using var master = await masterFactory.CreateDbContextAsync(ct);
        var tenant = await master.Tenants.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Tenant", id);
        if (tenant.Status is not (TenantStatus.Failed or TenantStatus.Provisioning))
            throw new ConflictException("Only failed or provisioning tenants can be provisioned.");
        // Serialize retries across API instances. This control-plane lock remains held
        // while creating/seeding a separate database; CREATE DATABASE is never transactional.
        await using var control = await master.Database.BeginTransactionAsync(ct);
        await master.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({id.ToString()}))", ct);
        await master.Entry(tenant).ReloadAsync(ct);
        if (tenant.Status is not (TenantStatus.Failed or TenantStatus.Provisioning))
            throw new ConflictException("Tenant provisioning has already completed.");
        var existingDatabase = false;
        try
        {
            var provisionConnection = configuration.GetConnectionString("TenantProvisioning");
            var server = string.IsNullOrWhiteSpace(provisionConnection)
                ? configuration.GetConnectionString("TenantServer")! : provisionConnection;
            // Validates the registry name even on retry before it becomes an SQL identifier.
            _ = TenantResolver.BuildConnectionString(server, tenant.DatabaseName);
            await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ConnectionString))
            {
                await connection.OpenAsync(ct);
                await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @name)", connection);
                exists.Parameters.AddWithValue("name", tenant.DatabaseName);
                existingDatabase = (bool)(await exists.ExecuteScalarAsync(ct))!;
                if (!existingDatabase && (string.IsNullOrWhiteSpace(request.AdminEmail) || string.IsNullOrWhiteSpace(request.AdminPin)))
                    throw new DomainValidationException("An administrator email and four-digit PIN are required to create a new database.");
                if (!existingDatabase)
                {
                    // Generated and regex-validated above; identifiers cannot be SQL parameters.
                    var owner = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("TenantServer")).Username
                        ?? throw new InvalidOperationException("TenantServer must specify Username.");
                    var quotedOwner = new NpgsqlCommandBuilder().QuoteIdentifier(owner);
                    await using var create = new NpgsqlCommand($"CREATE DATABASE \"{tenant.DatabaseName}\" OWNER {quotedOwner}", connection);
                    await create.ExecuteNonQueryAsync(ct);
                }
            }
            using var ambient = TenantExecution.Enter(tenant.Id, tenant.Code);
            var seedValues = new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "true",
                // An existing database keeps its own users; never seed or reset an admin there.
                ["Seed:AdminEmail"] = existingDatabase ? "" : request.AdminEmail!.Trim().ToLowerInvariant(),
                ["Seed:AdminPin"] = existingDatabase ? "" : request.AdminPin,
                ["Seed:AdminFirstName"] = tenant.Name,
                ["Seed:AdminLastName"] = "Admin",
            };
            foreach (var (name, value) in await TenantServices.MarketingSeedOverridesAsync(master, configuration, ct))
                seedValues[name] = value;
            var seed = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(seedValues).Build();
            await DatabaseInitializer.InitializeAsync(services, seed, tenant);
            // An existing database's users are not in the shared login registry yet, so keep the
            // client Suspended: run "Sync imported logins", then activate it.
            tenant.Status = existingDatabase ? TenantStatus.Suspended : TenantStatus.Active;
            tenant.ProvisionedAtUtc = DateTime.UtcNow;
            await master.SaveChangesAsync(ct);
            await control.CommitAsync(ct);
            return tenant;
        }
        catch (Exception ex)
        {
            // Keep failed databases and registry rows for repair/retry; never drop data.
            await control.RollbackAsync(CancellationToken.None);
            tenant.Status = TenantStatus.Failed;
            await master.SaveChangesAsync(CancellationToken.None);
            logger.LogError(ex, "Provisioning failed for tenant {TenantId}; database retained for retry.", id);
            if (ex is AppException) throw;
            throw new ExternalServiceException("Tenant provisioning failed. Review server logs, then retry this tenant.");
        }
    }
}
