using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Dto.Marketing
{
    /// <summary>Public — no login required. A prospective academy asking to see the platform.</summary>
    public class CreatePlatformDemoRequestRequest
    {
        [Required]
        [MaxLength(150)]
        public string FullName { get; set; } = null!;

        [Required]
        [EmailAddress]
        [MaxLength(256)]
        public string WorkEmail { get; set; } = null!;

        [Required]
        [MaxLength(20)]
        public string Phone { get; set; } = null!;

        [Required]
        [MaxLength(150)]
        public string AcademyName { get; set; } = null!;

        [MaxLength(1000)]
        public string? Message { get; set; }
    }

    public class PlatformDemoRequestDto
    {
        public Guid Id { get; set; }

        public string FullName { get; set; } = null!;

        public string WorkEmail { get; set; } = null!;

        public string Phone { get; set; } = null!;

        public string AcademyName { get; set; } = null!;

        public string? Message { get; set; }

        public StoreInquiryStatus Status { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    public class UpdatePlatformDemoRequestStatusRequest
    {
        [Required]
        public StoreInquiryStatus Status { get; set; }
    }

    // --- Blog ---

    public class CreateBlogPostRequest
    {
        [Required]
        [MaxLength(200)]
        public string Title { get; set; } = null!;

        /// <summary>Optional — auto-derived from Title (lowercase, hyphenated) when omitted.</summary>
        [MaxLength(200)]
        public string? Slug { get; set; }

        [Required]
        [MaxLength(500)]
        public string Excerpt { get; set; } = null!;

        [Required]
        public string Content { get; set; } = null!;

        public bool IsPublished { get; set; }
    }

    public class UpdateBlogPostRequest
    {
        [Required]
        [MaxLength(200)]
        public string Title { get; set; } = null!;

        [Required]
        [MaxLength(200)]
        public string Slug { get; set; } = null!;

        [Required]
        [MaxLength(500)]
        public string Excerpt { get; set; } = null!;

        [Required]
        public string Content { get; set; } = null!;

        public bool IsPublished { get; set; }
    }

    /// <summary>Admin view — every field, published or not.</summary>
    public class BlogPostDto
    {
        public Guid Id { get; set; }

        public string Title { get; set; } = null!;

        public string Slug { get; set; } = null!;

        public string Excerpt { get; set; } = null!;

        public string Content { get; set; } = null!;

        public int ReadMinutes { get; set; }

        public bool IsPublished { get; set; }

        public DateTime? PublishedAtUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>Public list card — no full Content, keeps the /blog listing payload small.</summary>
    public class BlogPostSummaryDto
    {
        public Guid Id { get; set; }

        public string Title { get; set; } = null!;

        public string Slug { get; set; } = null!;

        public string Excerpt { get; set; } = null!;

        public int ReadMinutes { get; set; }

        public DateTime PublishedAtUtc { get; set; }
    }

    /// <summary>Public single-post view — published posts only.</summary>
    public class BlogPostDetailDto
    {
        public string Title { get; set; } = null!;

        public string Slug { get; set; } = null!;

        public string Excerpt { get; set; } = null!;

        public string Content { get; set; } = null!;

        public int ReadMinutes { get; set; }

        public DateTime PublishedAtUtc { get; set; }
    }
}
