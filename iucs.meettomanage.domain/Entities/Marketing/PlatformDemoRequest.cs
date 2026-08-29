using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.domain.Entities.Marketing
{
    /// <summary>
    /// A prospective academy owner asking to see the Meet to Manage PLATFORM itself —
    /// evaluating whether to deploy it for their own business. Distinct from
    /// Admission.DemoBooking, which is a free trial CLASS an existing academy's own
    /// parent books for their child; this is a B2B sales lead, not a class attendee.
    /// </summary>
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
    }
}
