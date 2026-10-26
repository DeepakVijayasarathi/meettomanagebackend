using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Communication;

/// <summary>
/// One privacy-safe presence row per signed-in user and UTC day. This preserves an accurate daily-active trend
/// without storing individual heartbeat events or device identifiers beyond the installation status table.
/// </summary>
[Index(nameof(UserId), nameof(ActivityDate), IsUnique = true)]
[Index(nameof(ActivityDate))]
public sealed class MobileAppDailyActivity : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateOnly ActivityDate { get; set; }
    [MaxLength(24)] public string Role { get; set; } = null!;
}
