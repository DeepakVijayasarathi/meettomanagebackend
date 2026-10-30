using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;

namespace iucs.meettomanage.domain.Entities.Marketing;

public class MarketingLeadActivity : BaseEntity
{
    public Guid PlatformDemoRequestId { get; set; }
    public PlatformDemoRequest PlatformDemoRequest { get; set; } = null!;
    [MaxLength(50)] public string Type { get; set; } = null!;
    [MaxLength(1000)] public string Detail { get; set; } = null!;
    public Guid? ActorUserId { get; set; }
}
