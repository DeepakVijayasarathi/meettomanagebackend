using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.domain.Entities.Users
{
    /// <summary>
    /// Student record under a parent account. Children have no login of their own.
    /// </summary>
    public class Child : AuditEntity
    {
        public Guid ParentProfileId { get; set; }

        public ParentProfile ParentProfile { get; set; } = null!;

        [MaxLength(100)]
        public string FirstName { get; set; } = null!;

        [MaxLength(100)]
        public string LastName { get; set; } = null!;

        public DateOnly? DateOfBirth { get; set; }

        public Gender? Gender { get; set; }

        /// <summary>Current academic level, seeded from demo feedback and updated over time.</summary>
        [MaxLength(100)]
        public string? AcademicLevel { get; set; }

        /// <summary>
        /// Special enrolment notes from the Relationship Manager, surfaced on the child's
        /// profile (e.g. "enrolled during discount window; services start after 4 months").
        /// </summary>
        [MaxLength(2000)]
        public string? RmNotes { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
