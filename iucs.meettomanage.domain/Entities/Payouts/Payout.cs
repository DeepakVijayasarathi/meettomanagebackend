using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Payouts
{
    /// <summary>
    /// One teacher's payout for one calendar month. Every new month starts a fresh
    /// calculation; previous months are never modified. The finalized statement is
    /// emailed to the teacher. Visibility: admin sees all, a teacher sees only their own.
    /// </summary>
    [Index(nameof(TeacherProfileId), nameof(PeriodYear), nameof(PeriodMonth), IsUnique = true)]
    public class Payout : AuditEntity
    {
        public Guid TeacherProfileId { get; set; }

        public TeacherProfile TeacherProfile { get; set; } = null!;

        public int PeriodYear { get; set; }

        public int PeriodMonth { get; set; }

        public PayoutStatus Status { get; set; } = PayoutStatus.Pending;

        /// <summary>Denormalised sum of items, locked at finalization.</summary>
        public decimal TotalAmount { get; set; }

        public DateTime? FinalizedAtUtc { get; set; }

        public DateTime? EmailSentAtUtc { get; set; }

        public ICollection<PayoutItem> Items { get; set; } = new List<PayoutItem>();
    }
}
