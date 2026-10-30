using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;

namespace iucs.meettomanage.domain.Entities.Marketing;

public class MarketingReportSetting : AuditEntity
{
    public bool Enabled { get; set; }
    [MaxLength(20)] public string Frequency { get; set; } = "Weekly";
    [MaxLength(256)] public string RecipientEmail { get; set; } = null!;
    public int RetentionDays { get; set; } = 400;
    public DateTime? LastSentAtUtc { get; set; }
}
