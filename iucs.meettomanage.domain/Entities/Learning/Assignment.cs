using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Learning
{
    /// <summary>
    /// Homework or a test set for a whole batch by its teacher (or staff). Every active student in
    /// the batch gets an <see cref="AssignmentSubmission"/> row when it is published.
    /// Test marks stay hidden from families until <see cref="ResultsReleasedAtUtc"/> is set.
    /// </summary>
    [Index(nameof(BatchId), nameof(DueAtUtc))]
    [Index(nameof(TeacherProfileId), nameof(CreatedAtUtc))]
    public class Assignment : AuditEntity
    {
        public Guid BatchId { get; set; }

        public Batch Batch { get; set; } = null!;

        /// <summary>The teacher responsible for grading — the batch's teacher when it was created.</summary>
        public Guid TeacherProfileId { get; set; }

        public TeacherProfile TeacherProfile { get; set; } = null!;

        public AssignmentKind Kind { get; set; }

        [MaxLength(200)]
        public string Title { get; set; } = null!;

        [MaxLength(4000)]
        public string? Instructions { get; set; }

        /// <summary>Optional worksheet stored via IFileStorage.</summary>
        [MaxLength(500)]
        public string? AttachmentPath { get; set; }

        [MaxLength(200)]
        public string? AttachmentFileName { get; set; }

        public DateTime? DueAtUtc { get; set; }

        /// <summary>Required for tests; optional for homework (ungraded homework is just marked done).</summary>
        [Precision(8, 2)]
        public decimal? MaxMarks { get; set; }

        /// <summary>Topic tag used by Mito and insights to group mistakes, e.g. "Fractions".</summary>
        [MaxLength(120)]
        public string? Topic { get; set; }

        public DateTime? ResultsReleasedAtUtc { get; set; }

        public ICollection<AssignmentSubmission> Submissions { get; set; } = new List<AssignmentSubmission>();
    }
}
