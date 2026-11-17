using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Marketing
{
    /// <summary>What a marketing-console user may do in the CRM. Stored by name.</summary>
    public enum CrmRole
    {
        /// <summary>Everything: all leads and customers, reassigning, team, settings and reports.</summary>
        Manager,
        /// <summary>Works their own leads plus unassigned ones; can't manage the team or settings.</summary>
        Sales,
        /// <summary>Read-only access to everything.</summary>
        Viewer,
    }

    public enum CrmTaskType { FollowUp, Call, Meeting, DemoPrep, Quote, Onboarding, Other }

    /// <summary>
    /// A marketing-console user's CRM role. A marketing user with no row is treated as a Manager, so the
    /// existing account keeps full access; people added from the Team page always get a row.
    /// </summary>
    [Index(nameof(UserId), IsUnique = true)]
    public class CrmTeamMember : AuditEntity
    {
        public Guid UserId { get; set; }
        public CrmRole Role { get; set; } = CrmRole.Sales;
    }

    /// <summary>A to-do with an owner and a due time, on a lead, on a customer academy, or on its own.</summary>
    [Index(nameof(AssignedToUserId), nameof(CompletedAtUtc), nameof(DueAtUtc))]
    [Index(nameof(LeadId))]
    [Index(nameof(TenantId))]
    public class CrmTask : AuditEntity
    {
        public Guid? LeadId { get; set; }
        /// <summary>A customer academy (master database tenant id).</summary>
        public Guid? TenantId { get; set; }
        [MaxLength(200)] public string Title { get; set; } = null!;
        public CrmTaskType Type { get; set; } = CrmTaskType.FollowUp;
        public DateTime DueAtUtc { get; set; }
        public Guid? AssignedToUserId { get; set; }
        [MaxLength(1000)] public string? Notes { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        [MaxLength(1000)] public string? Outcome { get; set; }
    }

    /// <summary>A note the team keeps about a customer academy.</summary>
    [Index(nameof(TenantId))]
    public class CrmCustomerNote : AuditEntity
    {
        public Guid TenantId { get; set; }
        [MaxLength(2000)] public string Text { get; set; } = null!;
        [MaxLength(150)] public string? Author { get; set; }
    }
}
