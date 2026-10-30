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
        [NotMapped] public bool IsDuplicate { get; set; }
        [NotMapped] public int EngagementScoreBoost { get; set; }
    }
}
