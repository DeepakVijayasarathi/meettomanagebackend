using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Academics
{
    /// <summary>
    /// Teacher leave application. The 6-hour-before-session restriction is enforced in the
    /// application layer at submission time; approval/rejection is an Admin workflow.
    /// </summary>
    [Index(nameof(TeacherProfileId), nameof(Status))]
    public class LeaveRequest : AuditEntity
    {
        public Guid TeacherProfileId { get; set; }

        public TeacherProfile TeacherProfile { get; set; } = null!;

        public DateTime StartAtUtc { get; set; }

        public DateTime EndAtUtc { get; set; }

        [MaxLength(1000)]
        public string Reason { get; set; } = null!;

        public LeaveStatus Status { get; set; } = LeaveStatus.Pending;

        public Guid? ReviewedBy { get; set; }

        public DateTime? ReviewedAtUtc { get; set; }

        [MaxLength(500)]
        public string? ReviewNote { get; set; }
    }
}
