using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Academics
{
    public enum WaitlistStatus
    {
        Waiting,
        /// <summary>A seat opened and the family and staff were told; waiting for staff to enrol.</summary>
        Offered,
        Enrolled,
        Removed
    }

    /// <summary>A student queued for a full batch. Queue order is <see cref="BaseEntity.CreatedAtUtc"/>.</summary>
    [Index(nameof(BatchId), nameof(ChildId), IsUnique = true)]
    [Index(nameof(Status), nameof(CreatedAtUtc))]
    public class BatchWaitlistEntry : AuditEntity
    {
        public Guid BatchId { get; set; }

        public Batch Batch { get; set; } = null!;

        public Guid ChildId { get; set; }

        public Child Child { get; set; } = null!;

        public WaitlistStatus Status { get; set; } = WaitlistStatus.Waiting;

        [MaxLength(500)]
        public string? Note { get; set; }

        public DateTime? OfferedAtUtc { get; set; }

        public DateTime? ClosedAtUtc { get; set; }
    }
}
