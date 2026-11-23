using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;

namespace iucs.meettomanage.domain.Entities.Marketing;

/// <summary>
/// A public-facing review for Meet to Manage (platform-level).
/// Collected from parents, partners, or imported from Capterra/G2/Google.
/// Used by the public marketing pages for social proof.
/// </summary>
public class Review : BaseEntity
{
    /// <summary>Platform source: "internal", "capterra", "g2", "google", "linkedin", "whatsapp"</summary>
    [Required, MaxLength(50)] public string Source { get; set; } = null!;

    /// <summary>Rating 1-5</summary>
    [Range(1, 5)] public int Rating { get; set; }

    /// <summary>Reviewer name (may be anonymized)</summary>
    [MaxLength(120)] public string AuthorName { get; set; } = "";

    /// <summary>Reviewer role / affiliation</summary>
    [MaxLength(200)] public string? AuthorRole { get; set; }

    /// <summary>Review text</summary>
    [MaxLength(2000)] public string Text { get; set; } = "";

    /// <summary>Public response from Meet to Manage (optional)</summary>
    [MaxLength(2000)] public string? Response { get; set; }

    /// <summary>URL to the original review (for backlink to Capterra/G2/Google)</summary>
    [MaxLength(1000)] public string? ReviewUrl { get; set; }

    /// <summary>Whether this review is visible on public pages</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>For internal tracking: linked academy (if applicable)</summary>
    public Guid? AcademyTenantId { get; set; }
}

/// <summary>
/// Tracks outbound review requests sent to academies/parents.
/// Used to systematically collect reviews from happy customers.
/// </summary>
public class ReviewRequest : BaseEntity
{
    /// <summary>Who we're requesting a review from</summary>
    [Required, MaxLength(200)] public string ContactName { get; set; } = null!;

    /// <summary>Contact method: "email" or "whatsapp"</summary>
    [Required, MaxLength(20)] public string ContactMethod { get; set; } = null!;

    /// <summary>Email or phone number</summary>
    [MaxLength(256)] public string ContactValue { get; set; } = null!;

    /// <summary>Which platform to request a review on</summary>
    [Required, MaxLength(50)] public string Platform { get; set; } = null!;

    /// <summary>Status: "pending", "sent", "replied", "declined", "expired"</summary>
    [Required, MaxLength(20)] public string Status { get; set; } = null!;

    /// <summary>Pre-filled review link sent to the contact</summary>
    [MaxLength(1000)] public string? SentUrl { get; set; }

    /// <summary>When the request was sent (null = not yet sent)</summary>
    public DateTime? SentAtUtc { get; set; }

    /// <summary>When this request expires (sent + 14 days)</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>For internal: linked academy tenant</summary>
    public Guid? AcademyTenantId { get; set; }
}
