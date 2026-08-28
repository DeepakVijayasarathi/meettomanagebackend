using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Academics
{
    /// <summary>Places a child into a batch; the source of classes completed/remaining on the parent dashboard.</summary>
    [Index(nameof(BatchId), nameof(ChildId), IsUnique = true)]
    public class BatchEnrollment : AuditEntity
    {
        public Guid BatchId { get; set; }

        public Batch Batch { get; set; } = null!;

        public Guid ChildId { get; set; }

        public Child Child { get; set; } = null!;

        public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Active;
    }
}
