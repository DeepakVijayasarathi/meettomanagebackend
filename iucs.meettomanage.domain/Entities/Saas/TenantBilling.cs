using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Tenancy;

namespace iucs.meettomanage.domain.Entities.Saas;

/// <summary>Who receives the tenant's invoices and reminders, and the details printed on them.</summary>
public class TenantBillingProfile
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    [MaxLength(200)] public string? LegalName { get; set; }
    [MaxLength(256)] public string? BillingEmail { get; set; }
    [MaxLength(30)] public string? BillingPhone { get; set; }
    [MaxLength(20)] public string? Gstin { get; set; }
    [MaxLength(500)] public string? Address { get; set; }
    [MaxLength(60)] public string? State { get; set; }
    [MaxLength(2)] public string Country { get; set; } = "IN";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Daily usage figures collected from the tenant's own database.</summary>
public class TenantUsageSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateOnly SnapshotDate { get; set; }
    public int ActiveStudents { get; set; }
    public int StaffCount { get; set; }
    public long StorageBytes { get; set; }
    /// <summary>Live-session minutes in the calendar month up to the snapshot date.</summary>
    public int SessionMinutesMonth { get; set; }
    public int SessionsMonth { get; set; }
    public int RecordingsMonth { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One row per reminder; the unique (subscription, rule, cycle, channel) key makes every
/// reminder at-most-once even with several API instances running.
/// </summary>
public class SubscriptionReminderLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SubscriptionId { get; set; }
    [MaxLength(64)] public string RuleKey { get; set; } = null!;
    [MaxLength(128)] public string CycleKey { get; set; } = null!;
    [MaxLength(20)] public string Channel { get; set; } = "email";
    [MaxLength(256)] public string? Recipient { get; set; }
    public ReminderLogStatus Status { get; set; }
    public int Attempts { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
}

/// <summary>Editable subject/body for a platform billing email. Placeholders look like {{tenant_name}}.</summary>
public class PlatformEmailTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string Key { get; set; } = null!;
    [MaxLength(300)] public string Subject { get; set; } = null!;
    public string Body { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
