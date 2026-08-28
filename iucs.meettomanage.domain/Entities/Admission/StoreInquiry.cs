using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Billing;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.domain.Entities.Admission
{
    /// <summary>
    /// A "Enroll now" submission from the public course catalog (/store) — an anonymous
    /// visitor's interest in a plan, before any account exists for them. The admission
    /// team follows up and, once ready, creates the parent/child account and subscription
    /// through the normal staff-mediated flow; nothing here auto-creates a paying account.
    /// </summary>
    public class StoreInquiry : AuditEntity
    {
        public Guid PackagePlanId { get; set; }

        public PackagePlan PackagePlan { get; set; } = null!;

        [MaxLength(150)]
        public string ParentName { get; set; } = null!;

        [MaxLength(256)]
        public string ParentEmail { get; set; } = null!;

        [MaxLength(20)]
        public string ParentPhone { get; set; } = null!;

        [MaxLength(150)]
        public string ChildName { get; set; } = null!;

        public int? ChildAge { get; set; }

        [MaxLength(1000)]
        public string? Notes { get; set; }

        public StoreInquiryStatus Status { get; set; } = StoreInquiryStatus.New;
    }
}
