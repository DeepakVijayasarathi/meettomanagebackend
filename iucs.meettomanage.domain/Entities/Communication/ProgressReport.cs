using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Communication
{
    /// <summary>
    /// One child's progress report for one calendar month. Staff write the content by
    /// hand (v1 — no AI drafting yet, since there's no per-child session-note data to
    /// draft from) and send it as an email to the parent. Every new month is a fresh
    /// row; a sent report's content is never edited afterward.
    /// </summary>
    [Index(nameof(ChildId), nameof(PeriodYear), nameof(PeriodMonth), IsUnique = true)]
    public class ProgressReport : AuditEntity
    {
        public Guid ChildId { get; set; }

        public Child Child { get; set; } = null!;

        public int PeriodYear { get; set; }

        public int PeriodMonth { get; set; }

        public ProgressReportStatus Status { get; set; } = ProgressReportStatus.Draft;

        /// <summary>Staff-authored report body (plain text, rendered into the email template).</summary>
        [MaxLength(8000)]
        public string Content { get; set; } = string.Empty;

        public DateTime? SentAtUtc { get; set; }
    }
}
