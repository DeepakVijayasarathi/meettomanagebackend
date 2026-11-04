using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Communication;

/// <summary>
/// One distinct family-app error, grouped by fingerprint per app version and platform. Details are scrubbed on the
/// server (emails, numbers, tokens and URL queries removed) and no user or installation id is kept, so rows can be
/// shown to operators. Deleted 90 days after they were last seen.
/// </summary>
[Index(nameof(Fingerprint), nameof(AppVersion), nameof(Platform), IsUnique = true)]
[Index(nameof(LastSeenAtUtc))]
public class MobileAppCrash : BaseEntity
{
    [MaxLength(64)] public string Fingerprint { get; set; } = null!;
    [MaxLength(16)] public string Platform { get; set; } = null!;
    [MaxLength(32)] public string AppVersion { get; set; } = null!;
    [MaxLength(120)] public string Name { get; set; } = null!;
    [MaxLength(300)] public string Message { get; set; } = null!;
    [MaxLength(64)] public string? Context { get; set; }
    [MaxLength(1500)] public string? StackTop { get; set; }
    /// <summary>True when the error took down the screen (error boundary or global handler), not a handled failure.</summary>
    public bool Fatal { get; set; }
    public int Occurrences { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}
