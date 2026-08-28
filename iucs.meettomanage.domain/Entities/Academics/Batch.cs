using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Academics
{
    [Index(nameof(Status))]
    public class Batch : AuditEntity
    {
        public Guid CourseId { get; set; }

        public Course Course { get; set; } = null!;

        public Guid TeacherProfileId { get; set; }

        public TeacherProfile TeacherProfile { get; set; } = null!;

        [MaxLength(150)]
        public string Name { get; set; } = null!;

        /// <summary>Maximum students; 1 for individual batches.</summary>
        public int Capacity { get; set; }

        public BatchStatus Status { get; set; } = BatchStatus.Active;

        public DateOnly? StartDate { get; set; }

        public DateOnly? EndDate { get; set; }

        /// <summary>Set when all course sessions finish; anchors the 15-day recording access window.</summary>
        public DateTime? CompletedAtUtc { get; set; }

        public ICollection<BatchEnrollment> Enrollments { get; set; } = new List<BatchEnrollment>();
    }
}
