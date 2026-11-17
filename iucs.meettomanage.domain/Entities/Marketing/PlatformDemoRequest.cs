using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Marketing
{
    /// <summary>
    /// A prospective academy owner asking to see the Meet to Manage PLATFORM itself —
    /// evaluating whether to deploy it for their own business. Distinct from
    /// Admission.DemoBooking, which is a free trial CLASS an existing academy's own
    /// parent books for their child; this is a B2B sales lead, not a class attendee.
    /// </summary>
    [Index(nameof(ArchivedAtUtc))]
    [Index(nameof(AssignedToUserId))]
    [Index(nameof(NextFollowUpAtUtc))]
    [Index(nameof(Stage))]
    [Index(nameof(DemoAtUtc))]
    public class PlatformDemoRequest : AuditEntity
    {
        [MaxLength(150)]
        public string FullName { get; set; } = null!;

        [MaxLength(256)]
        public string WorkEmail { get; set; } = null!;

        [MaxLength(20)]
        public string Phone { get; set; } = null!;

        [MaxLength(150)]
        public string AcademyName { get; set; } = null!;

        [MaxLength(1000)]
        public string? Message { get; set; }

        public StoreInquiryStatus Status { get; set; } = StoreInquiryStatus.New;

        [MaxLength(100)] public string? VisitorId { get; set; }
        [MaxLength(100)] public string? Source { get; set; }
        [MaxLength(100)] public string? Medium { get; set; }
        [MaxLength(100)] public string? Campaign { get; set; }
        [MaxLength(200)] public string? LandingPath { get; set; }
        [MaxLength(150)] public string? AssignedTo { get; set; }
        public DateTime? NextFollowUpAtUtc { get; set; }
        public DateTime? LastContactedAtUtc { get; set; }
        [MaxLength(1000)] public string? InternalNotes { get; set; }
        public Guid? AssignedToUserId { get; set; }
        public DateTime? ArchivedAtUtc { get; set; }
        public DateTime? LastReminderSentAtUtc { get; set; }

        // ---- Lifecycle automation
        public LeadStage Stage { get; set; } = LeadStage.New;
        public DateTime? StageChangedAtUtc { get; set; }
        /// <summary>When the team first marked the lead contacted; with CreatedAtUtc this gives the response time.</summary>
        public DateTime? FirstContactedAtUtc { get; set; }
        /// <summary>When the automatic "we got your request" e-mail went to the lead.</summary>
        public DateTime? AcknowledgedAtUtc { get; set; }
        public DateTime? DemoAtUtc { get; set; }
        public DateTime? DemoReminderDaySentAtUtc { get; set; }
        public DateTime? DemoReminderHourSentAtUtc { get; set; }
        public DateTime? DemoRecapSentAtUtc { get; set; }
        [MaxLength(200)] public string? LostReason { get; set; }
        /// <summary>The academy account this lead became, matched by e-mail (multi-tenant mode only).</summary>
        public Guid? TenantId { get; set; }
        [MaxLength(50)] public string? TenantCode { get; set; }
        /// <summary>When repeated pricing visits after a demo or during a trial flagged this lead as ready to buy.</summary>
        public DateTime? HighIntentAtUtc { get; set; }

        // ---- CRM: where they are and what the deal is worth
        [MaxLength(100)] public string? Region { get; set; }
        [MaxLength(100)] public string? City { get; set; }
        /// <summary>What they teach, e.g. Phonics, Abacus, Tuition.</summary>
        [MaxLength(100)] public string? Segment { get; set; }
        [MaxLength(300)] public string? Website { get; set; }
        public int? StudentCount { get; set; }
        /// <summary>Plan code the lead is likely to buy (basic, pro, growth, own-brand).</summary>
        [MaxLength(50)] public string? ExpectedPlan { get; set; }
        /// <summary>Expected monthly revenue in INR once they pay.</summary>
        public decimal? ExpectedMonthlyValue { get; set; }
        public DateTime? ExpectedCloseAtUtc { get; set; }
        [NotMapped] public bool IsDuplicate { get; set; }
        [NotMapped] public int EngagementScoreBoost { get; set; }
        /// <summary>Plain-language reasons behind <see cref="EngagementScoreBoost"/>, from the visitor's website journey.</summary>
        [NotMapped] public List<string> EngagementReasons { get; set; } = [];
    }
}
