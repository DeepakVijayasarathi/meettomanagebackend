using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Data;

/// <summary>The shared control plane; never constructed with a tenant connection.</summary>
public sealed class MasterDbContext(DbContextOptions<MasterDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantLoginAccount> LoginAccounts => Set<TenantLoginAccount>();
    public DbSet<TenantLoginIdentifier> LoginIdentifiers => Set<TenantLoginIdentifier>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var tenant = modelBuilder.Entity<Tenant>();
        tenant.ToTable("tenants");
        tenant.HasKey(x => x.Id);
        tenant.HasIndex(x => x.Code).IsUnique();
        tenant.HasIndex(x => x.DatabaseName).IsUnique();
        tenant.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);

        var account = modelBuilder.Entity<TenantLoginAccount>();
        account.ToTable("tenant_login_accounts");
        account.HasKey(x => x.UserId);
        account.Property(x => x.PinHash).HasMaxLength(512);
        account.HasAlternateKey(x => new { x.UserId, x.TenantId });
        account.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        var alias = modelBuilder.Entity<TenantLoginIdentifier>();
        alias.ToTable("tenant_login_identifiers");
        alias.HasKey(x => x.Identifier);
        alias.Property(x => x.Identifier).HasMaxLength(320);
        alias.HasIndex(x => new { x.TenantId, x.UserId });
        alias.HasOne(x => x.Account).WithMany().HasForeignKey(x => new { x.UserId, x.TenantId })
            .HasPrincipalKey(x => new { x.UserId, x.TenantId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
