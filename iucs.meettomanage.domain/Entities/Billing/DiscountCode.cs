using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Billing
{
    /// <summary>
    /// A promo code families or staff can apply to an unpaid invoice: a percentage or a fixed amount off, with
    /// optional validity window, usage cap, once-per-family rule and course restriction.
    /// </summary>
    [Index(nameof(Code), IsUnique = true)]
    public class DiscountCode : AuditEntity
    {
        /// <summary>Stored upper-case; matched case-insensitively.</summary>
        [MaxLength(32)]
        public string Code { get; set; } = null!;

        [MaxLength(200)]
        public string? Description { get; set; }

        /// <summary>Set one of these: a percentage (0-100) or a fixed amount in the invoice currency.</summary>
        [Precision(5, 2)]
        public decimal? PercentOff { get; set; }

        [Precision(12, 2)]
        public decimal? AmountOff { get; set; }

        public DateTime? ValidFromUtc { get; set; }

        public DateTime? ValidUntilUtc { get; set; }

        /// <summary>Total uses allowed across all families; null = unlimited.</summary>
        public int? MaxRedemptions { get; set; }

        public int RedemptionCount { get; set; }

        public bool OncePerFamily { get; set; } = true;

        /// <summary>When set, only invoices for this course qualify.</summary>
        public Guid? CourseId { get; set; }

        public Course? Course { get; set; }

        public bool IsActive { get; set; } = true;
    }

    /// <summary>One use of a <see cref="DiscountCode"/> on an invoice.</summary>
    [Index(nameof(InvoiceId), IsUnique = true)]
    [Index(nameof(DiscountCodeId), nameof(ParentProfileId))]
    public class DiscountRedemption : BaseEntity
    {
        public Guid DiscountCodeId { get; set; }

        public DiscountCode DiscountCode { get; set; } = null!;

        public Guid InvoiceId { get; set; }

        public Invoice Invoice { get; set; } = null!;

        public Guid ParentProfileId { get; set; }

        public ParentProfile ParentProfile { get; set; } = null!;

        [Precision(12, 2)]
        public decimal Amount { get; set; }
    }
}
