using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Learning
{
    /// <summary>
    /// A course-completion certificate. The student and course names are copied at issue time so a later rename
    /// doesn't change a certificate already handed out. <see cref="Code"/> is what the QR code and the public
    /// verification page use.
    /// </summary>
    [Index(nameof(Code), IsUnique = true)]
    [Index(nameof(ChildId), nameof(BatchId), IsUnique = true)]
    public class Certificate : AuditEntity
    {
        public Guid ChildId { get; set; }

        public Child Child { get; set; } = null!;

        /// <summary>The batch the student completed; null for a certificate issued by hand without one.</summary>
        public Guid? BatchId { get; set; }

        public Batch? Batch { get; set; }

        [MaxLength(16)]
        public string Code { get; set; } = null!;

        [MaxLength(200)]
        public string StudentName { get; set; } = null!;

        [MaxLength(200)]
        public string CourseName { get; set; } = null!;

        [MaxLength(200)]
        public string? TeacherName { get; set; }

        public DateTime IssuedAtUtc { get; set; }

        public DateTime? RevokedAtUtc { get; set; }

        [MaxLength(300)]
        public string? RevokedReason { get; set; }
    }
}
