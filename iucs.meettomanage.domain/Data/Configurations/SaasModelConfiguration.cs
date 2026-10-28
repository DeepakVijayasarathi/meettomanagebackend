using System.Text;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace iucs.meettomanage.domain.Data.Configurations;

/// <summary>
/// Master-database mapping for the SaaS (platform billing) layer. Every table, column, key, index
/// and foreign key it creates is snake_case (tenant_subscription.tenant_id, plan_price.plan_id, ...).
/// The pre-existing registry tables (tenants, tenant_login_*) keep their original column names.
/// </summary>
public static class SaasModelConfiguration
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Plan>(e =>
        {
            e.ToTable("plan");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).IsRequired();
            e.HasMany(x => x.Prices).WithOne(x => x.Plan).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Entitlements).WithOne(x => x.Plan).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlanPrice>(e =>
        {
            e.ToTable("plan_price");
            e.HasKey(x => x.Id);
            e.Property(x => x.BillingInterval).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.HasIndex(x => new { x.PlanId, x.BillingInterval, x.IsActive });
        });

        modelBuilder.Entity<PlanEntitlement>(e =>
        {
            e.ToTable("plan_entitlement");
            e.HasKey(x => x.Id);
            e.Property(x => x.ValueType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.PlanId, x.Key }).IsUnique();
        });

        modelBuilder.Entity<TenantSubscription>(e =>
        {
            e.ToTable("tenant_subscription");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.HasIndex(x => x.TenantId).IsUnique();
            e.HasIndex(x => new { x.Status, x.CurrentPeriodEndUtc });
            e.HasIndex(x => x.TrialEndsAtUtc);
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PlanPrice).WithMany().HasForeignKey(x => x.PlanPriceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantSubscriptionEvent>(e =>
        {
            e.ToTable("tenant_subscription_event");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<TenantEntitlementOverride>(e =>
        {
            e.ToTable("tenant_entitlement_override");
            e.HasKey(x => x.Id);
            e.Property(x => x.ValueType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlatformInvoice>(e =>
        {
            e.ToTable("platform_invoice");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Subtotal).HasPrecision(12, 2);
            e.Property(x => x.TaxRatePercent).HasPrecision(5, 2);
            e.Property(x => x.TaxAmount).HasPrecision(12, 2);
            e.Property(x => x.Total).HasPrecision(12, 2);
            e.HasIndex(x => x.InvoiceNumber).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.IssuedAtUtc });
            e.HasIndex(x => new { x.Status, x.DueAtUtc });
            e.HasIndex(x => x.GatewayOrderRef);
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne(x => x.Invoice).HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Payments).WithOne(x => x.Invoice).HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlatformInvoiceLine>(e =>
        {
            e.ToTable("platform_invoice_line");
            e.HasKey(x => x.Id);
            e.Property(x => x.UnitAmount).HasPrecision(12, 2);
            e.Property(x => x.Amount).HasPrecision(12, 2);
        });

        modelBuilder.Entity<PlatformPayment>(e =>
        {
            e.ToTable("platform_payment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.HasIndex(x => new { x.Gateway, x.GatewayPaymentRef });
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<PlatformWebhookEvent>(e =>
        {
            e.ToTable("platform_webhook_event");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Provider, x.ExternalEventId }).IsUnique();
        });

        modelBuilder.Entity<TenantBillingProfile>(e =>
        {
            e.ToTable("tenant_billing_profile");
            e.HasKey(x => x.TenantId);
            e.HasOne(x => x.Tenant).WithOne().HasForeignKey<TenantBillingProfile>(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantUsageSnapshot>(e =>
        {
            e.ToTable("tenant_usage_snapshot");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.SnapshotDate }).IsUnique();
        });

        modelBuilder.Entity<SubscriptionReminderLog>(e =>
        {
            e.ToTable("subscription_reminder_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.SubscriptionId, x.RuleKey, x.CycleKey, x.Channel }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<PlatformEmailTemplate>(e =>
        {
            e.ToTable("platform_email_template");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<PlatformAuditLog>(e =>
        {
            e.ToTable("platform_audit_log");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
        });

        ApplySnakeCaseNames(modelBuilder);
    }

    /// <summary>Renames columns, keys, indexes and foreign keys of the SaaS entities to snake_case.</summary>
    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        var saasNamespace = typeof(Plan).Namespace;
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(x => x.ClrType.Namespace == saasNamespace))
        {
            var table = entity.GetTableName()!;
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));

            foreach (var key in entity.GetKeys())
                key.SetName((key.IsPrimaryKey() ? "pk_" : "ak_") + table + KeySuffix(key.Properties, key.IsPrimaryKey()));

            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName((index.IsUnique ? "ux_" : "ix_") + table + "_" + Columns(index.Properties));

            foreach (var foreignKey in entity.GetForeignKeys())
                foreignKey.SetConstraintName("fk_" + table + "_"
                    + (foreignKey.PrincipalEntityType.GetTableName() ?? "") + "_" + Columns(foreignKey.Properties));
        }
    }

    private static string KeySuffix(IReadOnlyList<IMutableProperty> properties, bool primary) =>
        primary ? "" : "_" + Columns(properties);

    private static string Columns(IReadOnlyList<IMutableProperty> properties) =>
        string.Join("_", properties.Select(x => ToSnakeCase(x.Name)));

    /// <summary>TenantId -> tenant_id, CurrentPeriodEndUtc -> current_period_end_utc, GSTIN-style runs stay together.</summary>
    public static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previousLower = i > 0 && !char.IsUpper(name[i - 1]);
                var nextLower = i > 0 && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (i > 0 && (previousLower || nextLower)) builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else builder.Append(c);
        }
        return builder.ToString();
    }
}
