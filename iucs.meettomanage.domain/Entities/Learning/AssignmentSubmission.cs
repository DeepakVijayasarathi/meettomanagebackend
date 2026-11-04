using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Learning
{
    /// <summary>One student's work and result for an <see cref="Assignment"/>.</summary>
    [Index(nameof(AssignmentId), nameof(ChildId), IsUnique = true)]
    [Index(nameof(ChildId), nameof(Status))]
    public class AssignmentSubmission : AuditEntity
    {
        public Guid AssignmentId { get; set; }

        public Assignment Assignment { get; set; } = null!;

        public Guid ChildId { get; set; }

        public Child Child { get; set; } = null!;

        public AssignmentSubmissionStatus Status { get; set; } = AssignmentSubmissionStatus.Pending;

        [MaxLength(4000)]
        public string? AnswerText { get; set; }

        [MaxLength(500)]
        public string? AttachmentPath { get; set; }

        [MaxLength(200)]
        public string? AttachmentFileName { get; set; }

        public DateTime? SubmittedAtUtc { get; set; }

        [Precision(8, 2)]
        public decimal? Marks { get; set; }

        [MaxLength(2000)]
        public string? Feedback { get; set; }

        public DateTime? GradedAtUtc { get; set; }
    }
}
