using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;

namespace iucs.meettomanage.domain.Entities.Marketing;

public class MarketingCampaignBudget : AuditEntity
{
    [MaxLength(100)] public string Campaign { get; set; } = null!;
    public decimal Spend { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "INR";
    public decimal? WonRevenue { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
}
