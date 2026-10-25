using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Communication;

/// <summary>
/// Privacy-limited operational heartbeat for one app installation. InstallationId is an app-generated random UUID,
/// never a hardware identifier. No token, contact detail, device model, message, or form content is stored here.
/// </summary>
[Index(nameof(InstallationId), IsUnique = true)]
[Index(nameof(LastSeenAtUtc))]
[Index(nameof(UserId), nameof(LastSeenAtUtc))]
public sealed class MobileAppInstallation : BaseEntity
{
    [MaxLength(64)] public string InstallationId { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    [MaxLength(24)] public string Role { get; set; } = null!;
    [MaxLength(16)] public string Platform { get; set; } = null!;
    [MaxLength(32)] public string AppVersion { get; set; } = null!;
    [MaxLength(32)] public string? BuildNumber { get; set; }
    [MaxLength(64)] public string? RuntimeVersion { get; set; }
    public bool NotificationsEnabled { get; set; }
    public bool BiometricLockEnabled { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? LastSuccessfulSyncAtUtc { get; set; }
    [MaxLength(64)] public string? LastErrorCategory { get; set; }
    public int HeartbeatCount { get; set; }
}
