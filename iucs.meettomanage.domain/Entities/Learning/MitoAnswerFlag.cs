using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Learning
{
    /// <summary>
    /// A Mito answer a family reported as wrong or unhelpful, kept (with the question and answer they chose to
    /// send) for a teacher to review and correct. Only what the family explicitly flags is stored.
    /// </summary>
    [Index(nameof(ResolvedAtUtc), nameof(CreatedAtUtc))]
    [Index(nameof(ChildId))]
    public class MitoAnswerFlag : BaseEntity
    {
        public Guid ChildId { get; set; }

        public Child Child { get; set; } = null!;

        [MaxLength(16)]
        public string Mode { get; set; } = "general";

        [MaxLength(2000)]
        public string Question { get; set; } = null!;

        [MaxLength(4000)]
        public string Answer { get; set; } = null!;

        [MaxLength(500)]
        public string? Reason { get; set; }

        public DateTime? ResolvedAtUtc { get; set; }

        public Guid? ResolvedByUserId { get; set; }

        /// <summary>The teacher's correction or note, shown back to the family.</summary>
        [MaxLength(2000)]
        public string? Resolution { get; set; }
    }
}
