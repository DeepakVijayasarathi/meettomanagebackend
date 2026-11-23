using System.ComponentModel.DataAnnotations;

namespace iucs.meettomanage.application.Dto.Marketing
{
    /// <summary>Request to create a new review (platform admin only).</summary>
    public class CreateReviewRequest : IValidatableObject
    {
        [Required, MaxLength(50)] public string Source { get; set; } = null!;
        [Range(1, 5)] public int Rating { get; set; }
        [Required, MaxLength(120)] public string AuthorName { get; set; } = null!;
        [MaxLength(200)] public string? AuthorRole { get; set; }
        [Required, MaxLength(2000)] public string Text { get; set; } = null!;
        [MaxLength(2000)] public string? Response { get; set; }
        [MaxLength(1000)] public string? ReviewUrl { get; set; }
        public bool IsVisible { get; set; } = true;
        public Guid? AcademyTenantId { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (!new[] { "internal", "capterra", "g2", "google", "linkedin", "whatsapp" }.Contains(Source.ToLower()))
                yield return new ValidationResult("Invalid source. Must be: internal, capterra, g2, google, linkedin, or whatsapp.", [nameof(Source)]);
        }
    }

    /// <summary>Request to update a review (platform admin only).</summary>
    public class UpdateReviewRequest
    {
        [Range(1, 5)] public int? Rating { get; set; }
        [MaxLength(120)] public string? AuthorName { get; set; }
        [MaxLength(200)] public string? AuthorRole { get; set; }
        [MaxLength(2000)] public string? Text { get; set; }
        [MaxLength(2000)] public string? Response { get; set; }
        [MaxLength(1000)] public string? ReviewUrl { get; set; }
        public bool? IsVisible { get; set; }
    }

    /// <summary>Public-facing review DTO (no internal fields exposed).</summary>
    public class PublicReviewDto
    {
        public Guid Id { get; set; }
        public string Source { get; set; } = null!;
        public int Rating { get; set; }
        public string AuthorName { get; set; } = null!;
        public string? AuthorRole { get; set; }
        public string Text { get; set; } = null!;
        public string? Response { get; set; }
        public string? ReviewUrl { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>Full review DTO (platform admin - includes all fields).</summary>
    public class ReviewDto
    {
        public Guid Id { get; set; }
        public string Source { get; set; } = null!;
        public int Rating { get; set; }
        public string AuthorName { get; set; } = null!;
        public string? AuthorRole { get; set; }
        public string Text { get; set; } = null!;
        public string? Response { get; set; }
        public string? ReviewUrl { get; set; }
        public bool IsVisible { get; set; }
        public Guid? AcademyTenantId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>Aggregate rating summary for the homepage.</summary>
    public class AggregateRatingDto
    {
        public double AverageRating { get; set; }
        public int TotalReviews { get; set; }
        public int VisibleReviews { get; set; }
        public Dictionary<string, int> RatingBreakdown { get; set; } = new();
        public Dictionary<string, int> ReviewsBySource { get; set; } = new();
    }

    // ========== Review Requests ==========

    /// <summary>Request to create a review request (platform admin only).</summary>
    public class CreateReviewRequestRequest
    {
        [Required, MaxLength(200)] public string ContactName { get; set; } = null!;
        [Required, MaxLength(256)] public string ContactValue { get; set; } = null!;
        [Required, MaxLength(20)] public string ContactMethod { get; set; } = null!;
        [Required, MaxLength(50)] public string Platform { get; set; } = null!;
        public Guid? AcademyTenantId { get; set; }
    }

    /// <summary>Update the status of a review request.</summary>
    public class UpdateReviewRequestStatusRequest
    {
        [Required, MaxLength(20)] public string Status { get; set; } = null!;
    }

    /// <summary>Request to send a review request (triggers email/WhatsApp).</summary>
    public class SendReviewRequestRequest
    {
        [Required] public Guid ReviewRequestId { get; set; }
    }

    /// <summary>Review request DTO.</summary>
    public class ReviewRequestDto
    {
        public Guid Id { get; set; }
        public string ContactName { get; set; } = null!;
        public string ContactMethod { get; set; } = null!;
        public string ContactValue { get; set; } = null!;
        public string Platform { get; set; } = null!;
        public string Status { get; set; } = null!;
        public string? SentUrl { get; set; }
        public DateTime? SentAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public Guid? AcademyTenantId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
