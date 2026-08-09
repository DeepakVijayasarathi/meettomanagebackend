using System.ComponentModel.DataAnnotations;
using iucs.readernest.domain.Enums;

namespace iucs.readernest.application.Dto.Communication
{
    public class ProgressReportDto
    {
        public Guid Id { get; set; }

        public Guid ChildId { get; set; }

        public string ChildName { get; set; } = null!;

        public Guid ParentUserId { get; set; }

        public string ParentName { get; set; } = null!;

        public string ParentEmail { get; set; } = null!;

        public int PeriodYear { get; set; }

        public int PeriodMonth { get; set; }

        public ProgressReportStatus Status { get; set; }

        public string Content { get; set; } = null!;

        public DateTime? SentAtUtc { get; set; }

        public DateTime? UpdatedAtUtc { get; set; }
    }

    /// <summary>Draft content only — a sent report is locked and can't be re-saved.</summary>
    public class SaveProgressReportContentRequest
    {
        [Required]
        [MaxLength(8000)]
        public string Content { get; set; } = null!;
    }
}
