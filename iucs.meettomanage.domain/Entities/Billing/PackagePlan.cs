using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.domain.Entities.Billing
{
    /// <summary>Sellable package: subscription, session-based or one-time charging.</summary>
    public class PackagePlan : AuditEntity
    {
        [MaxLength(150)]
        public string Name { get; set; } = null!;

        public Guid? CourseId { get; set; }

        public Course? Course { get; set; }

        public BillingType BillingType { get; set; }

        public BillingCycle BillingCycle { get; set; }

        public decimal Price { get; set; }

        public int? SessionsIncluded { get; set; }

        /// <summary>Days of access a subscription on this plan gets from its start date; null means it never expires on its own.</summary>
        public int? ValidityDays { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
