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
    [Required, EmailAddress, MaxLength(256)] public string AdminEmail { get; set; } = "";
    [Required, RegularExpression(@"^\d{4}$")] public string AdminPin { get; set; } = "";
}

public sealed class TenantProvisioner(IServiceProvider services, IDbContextFactory<MasterDbContext> masterFactory,
    IConfiguration configuration, ILogger<TenantProvisioner> logger)
{
    public async Task<Tenant> CreateAsync(CreateTenantRequest request, CancellationToken ct)
    {
        await using var master = await masterFactory.CreateDbContextAsync(ct);
        var tenant = new Tenant
        {
            Code = request.Code, Name = request.Name.Trim(), DatabaseName = "mt_" + Guid.NewGuid().ToString("N"),
        };
        master.Tenants.Add(tenant);
        try { await master.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("The tenant code is already registered."); }
        return await ProvisionAsync(tenant.Id, request, ct);
    }

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
                if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
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
            var seed = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "true",
                ["Seed:AdminEmail"] = request.AdminEmail.Trim().ToLowerInvariant(),
                ["Seed:AdminPin"] = request.AdminPin,
                ["Seed:AdminFirstName"] = tenant.Name,
                ["Seed:AdminLastName"] = "Admin",
            }).Build();
            await DatabaseInitializer.InitializeAsync(services, seed, tenant);
            tenant.Status = TenantStatus.Active;
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
