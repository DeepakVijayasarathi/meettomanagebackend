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

        [MaxLength(100)] public string? VisitorId { get; set; }
        [MaxLength(100)] public string? Source { get; set; }
        [MaxLength(100)] public string? Medium { get; set; }
        [MaxLength(100)] public string? Campaign { get; set; }
        [MaxLength(200)] public string? LandingPath { get; set; }
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

        public string? Source { get; set; }
        public string? Medium { get; set; }
        public string? Campaign { get; set; }
        public string? LandingPath { get; set; }
        public string? AssignedTo { get; set; }
        public DateTime? NextFollowUpAtUtc { get; set; }
        public DateTime? LastContactedAtUtc { get; set; }
        public string? InternalNotes { get; set; }
        public int LeadScore { get; set; }
        public string LeadTemperature { get; set; } = null!;
        public bool IsDuplicate { get; set; }
    }

    public class UpdatePlatformDemoRequestStatusRequest
    {
        [Required]
        public StoreInquiryStatus Status { get; set; }
    }

    public class UpdatePlatformDemoRequestRequest
    {
        public StoreInquiryStatus? Status { get; set; }
        [MaxLength(150)] public string? AssignedTo { get; set; }
        public DateTime? NextFollowUpAtUtc { get; set; }
        [MaxLength(1000)] public string? InternalNotes { get; set; }
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
        /// <summary>
        /// When a published post goes live. Omit (or a past time) = live now; a future time =
        /// scheduled — hidden from the public blog until then, no background job needed.
        /// </summary>
        public DateTime? PublishAtUtc { get; set; }
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
        /// <summary>
        /// When a published post goes live. Omit (or a past time) = live now; a future time =
        /// scheduled — hidden from the public blog until then, no background job needed.
        /// </summary>
        public DateTime? PublishAtUtc { get; set; }
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

        /// <summary>"Draft", "Scheduled" (published, PublishedAtUtc still in the future) or "Published".</summary>
        public string Status { get; set; } = "Draft";

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

    /// <summary>One message in the marketing chat — "user" (the visitor) or "assistant".</summary>
    public class SalesChatTurn
    {
        [Required]
        [RegularExpression("^(user|assistant)$")]
        public string Role { get; set; } = null!;

        [Required]
        [MaxLength(1000)]
        public string Text { get; set; } = null!;
    }

    /// <summary>
    /// Public — no login required. The whole conversation so far (the server keeps no chat
    /// state), capped so one visitor can't run up an unbounded model bill per request.
    /// </summary>
    public class SalesChatRequest
    {
        [Required]
        [MinLength(1)]
        [MaxLength(20)]
        public List<SalesChatTurn> Messages { get; set; } = new();

        public MarketingLeadAttributionDto? Attribution { get; set; }
    }

    public class MarketingLeadAttributionDto
    {
        [MaxLength(100)] public string? VisitorId { get; set; }
        [MaxLength(100)] public string? Source { get; set; }
        [MaxLength(100)] public string? Medium { get; set; }
        [MaxLength(100)] public string? Campaign { get; set; }
        [MaxLength(200)] public string? LandingPath { get; set; }
    }

    public class SalesChatResponse
    {
        public string Reply { get; set; } = null!;

        /// <summary>Set when the assistant booked a platform demo during this turn — the visitor's name, so the site can confirm it.</summary>
        public string? BookedName { get; set; }
    }

    public class MarketingTranslationRequest
    {
        [Required, MinLength(1), MaxLength(30)]
        public List<string> Texts { get; set; } = [];

        [Required, StringLength(10)]
        public string TargetLanguage { get; set; } = string.Empty;
    }

    public class MarketingTranslationResponse
    {
        public IReadOnlyList<string> Translations { get; set; } = [];
    }

    /// <summary>Public — one anonymous marketing-site event, sent by the site itself.</summary>
    public class TrackMarketingEventRequest
    {
        [Required]
        [RegularExpression("^(page_view|chat_started)$")]
        public string Type { get; set; } = null!;

        [Required]
        [MaxLength(200)]
        public string Path { get; set; } = null!;

        /// <summary>The browser's random analytics id (localStorage) — never tied to a person.</summary>
        [Required]
        [RegularExpression("^[A-Za-z0-9-]{8,64}$")]
        public string VisitorId { get; set; } = null!;

        /// <summary>Host of an external referrer (e.g. "www.google.com"); omitted for direct/internal.</summary>
        [MaxLength(200)]
        public string? ReferrerHost { get; set; }

        [MaxLength(100)]
        public string? UtmSource { get; set; }

        [MaxLength(100)]
        public string? UtmMedium { get; set; }

        [MaxLength(100)]
        public string? UtmCampaign { get; set; }

        /// <summary>Browser-locale ISO country/region code. This is self-reported, not IP geolocation.</summary>
        [RegularExpression("^[A-Za-z]{2}$")]
        public string? Country { get; set; }
    }

    public class NamedCountDto
    {
        public string Name { get; set; } = null!;

        public int Count { get; set; }
    }

    public class MarketingPerformanceDto
    {
        public string Name { get; set; } = null!;
        public int Leads { get; set; }
        public int Visitors { get; set; }
        public int Converted { get; set; }
        public double LeadConversionRate { get; set; }
        public double ConversionRate { get; set; }
    }

    public class MarketingDailyPointDto
    {
        /// <summary>Local calendar day (yyyy-MM-dd) in the caller's time zone offset.</summary>
        public string Date { get; set; } = null!;

        public int PageViews { get; set; }

        public int Visitors { get; set; }

        public int DemoRequests { get; set; }

        public int ChatsStarted { get; set; }

        public double ConversionRate { get; set; }

        public double PagesPerVisitor { get; set; }
    }

    public class MarketingTotalsDto
    {
        public int PageViews { get; set; }

        public int Visitors { get; set; }

        public int ChatsStarted { get; set; }

        public int DemoRequests { get; set; }

        /// <summary>Demo requests the chat assistant booked itself (a subset of DemoRequests).</summary>
        public int ChatBookings { get; set; }

        /// <summary>Demo requests per 100 unique visitors.</summary>
        public double ConversionRate { get; set; }

        public double PagesPerVisitor { get; set; }

        public double ChatStartRate { get; set; }

        public double DemoPipelineConversionRate { get; set; }
    }

    /// <summary>The marketing console's analytics dashboard for the last <see cref="Days"/> days.</summary>
    public class MarketingDashboardDto
    {
        public int Days { get; set; }

        public string StartDate { get; set; } = null!;

        public string EndDate { get; set; } = null!;

        public MarketingTotalsDto Totals { get; set; } = new();

        /// <summary>Same figures for the equal-length period just before, for trend arrows.</summary>
        public MarketingTotalsDto Previous { get; set; } = new();

        public List<MarketingDailyPointDto> Daily { get; set; } = new();

        public List<NamedCountDto> TopPages { get; set; } = new();

        public List<NamedCountDto> TopBlogPosts { get; set; } = new();

        public List<NamedCountDto> Sources { get; set; } = new();

        public List<NamedCountDto> Campaigns { get; set; } = new();

        public List<NamedCountDto> Mediums { get; set; } = new();

        public List<NamedCountDto> Countries { get; set; } = new();

        public List<NamedCountDto> Devices { get; set; } = new();

        public List<NamedCountDto> DemoRequestsByStatus { get; set; } = new();
        public List<MarketingPerformanceDto> SourceQuality { get; set; } = new();
        public List<MarketingPerformanceDto> CampaignPerformance { get; set; } = new();
        public List<MarketingPerformanceDto> LandingPagePerformance { get; set; } = new();
    }

    public class MarketingAiInsightsDto
    {
        public string Summary { get; set; } = null!;
        public List<string> Observations { get; set; } = new();
        public List<string> NextActions { get; set; } = new();
        public bool AiPowered { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }
}
