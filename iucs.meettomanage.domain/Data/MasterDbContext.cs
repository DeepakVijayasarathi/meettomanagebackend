using iucs.meettomanage.domain.Data.Configurations;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Data;

/// <summary>The shared control plane; never constructed with a tenant connection.</summary>
public sealed class MasterDbContext(DbContextOptions<MasterDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantLoginAccount> LoginAccounts => Set<TenantLoginAccount>();
    public DbSet<TenantLoginIdentifier> LoginIdentifiers => Set<TenantLoginIdentifier>();

    // SaaS (platform billing) layer -- snake_case tables, see SaasModelConfiguration.
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanPrice> PlanPrices => Set<PlanPrice>();
    public DbSet<PlanEntitlement> PlanEntitlements => Set<PlanEntitlement>();
    public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();
    public DbSet<TenantSubscriptionEvent> TenantSubscriptionEvents => Set<TenantSubscriptionEvent>();
    public DbSet<TenantEntitlementOverride> TenantEntitlementOverrides => Set<TenantEntitlementOverride>();
    public DbSet<PlatformInvoice> PlatformInvoices => Set<PlatformInvoice>();
    public DbSet<PlatformInvoiceLine> PlatformInvoiceLines => Set<PlatformInvoiceLine>();
    public DbSet<PlatformPayment> PlatformPayments => Set<PlatformPayment>();
    public DbSet<PlatformWebhookEvent> PlatformWebhookEvents => Set<PlatformWebhookEvent>();
    public DbSet<TenantBillingProfile> TenantBillingProfiles => Set<TenantBillingProfile>();
    public DbSet<TenantUsageSnapshot> TenantUsageSnapshots => Set<TenantUsageSnapshot>();
    public DbSet<SubscriptionReminderLog> SubscriptionReminderLogs => Set<SubscriptionReminderLog>();
    public DbSet<PlatformEmailTemplate> PlatformEmailTemplates => Set<PlatformEmailTemplate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        var tenant = modelBuilder.Entity<Tenant>();
        tenant.ToTable("tenants");
        tenant.HasKey(x => x.Id);
        tenant.HasIndex(x => x.Code).IsUnique();
        tenant.HasIndex(x => x.DatabaseName).IsUnique();
        tenant.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        tenant.Property(x => x.AppAccessGranted).HasColumnName("app_access_granted");

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

        // Last, so foreign keys name the final (already mapped) principal tables.
        SaasModelConfiguration.Apply(modelBuilder);
    }
}
