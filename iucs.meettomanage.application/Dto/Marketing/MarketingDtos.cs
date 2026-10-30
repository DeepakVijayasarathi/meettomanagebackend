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

        /// <summary>Plain-language reasons behind LeadScore (message, chat booking, journey on the website, recency).</summary>
        public List<string> ScoreReasons { get; set; } = new();
        public Guid? AssignedToUserId { get; set; }
        public DateTime? ArchivedAtUtc { get; set; }
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
        public Guid? AssignedToUserId { get; set; }
    }

    public class MarketingLeadActivityDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = null!;
        public string Detail { get; set; } = null!;
        public DateTime CreatedAtUtc { get; set; }
    }

    public class MarketingLeadDetailDto
    {
        public PlatformDemoRequestDto Lead { get; set; } = new();
        public List<MarketingLeadActivityDto> Activity { get; set; } = new();
    }

    public class MarketingLeadQuery
    {
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 25;
        public string? Search { get; set; }
        public StoreInquiryStatus? Status { get; set; }
        public string? Source { get; set; }
        public Guid? AssignedToUserId { get; set; }
        public bool OverdueOnly { get; set; }
        public bool ArchivedOnly { get; set; }
    }

    public class BulkMarketingLeadRequest
    {
        [Required, MinLength(1), MaxLength(200)] public List<Guid> Ids { get; set; } = [];
        public StoreInquiryStatus? Status { get; set; }
        public Guid? AssignedToUserId { get; set; }
        public DateTime? NextFollowUpAtUtc { get; set; }
        public bool Archive { get; set; }
    }

    public class MarketingLeadRecommendationDto
    {
        public string Summary { get; set; } = null!;
        public string NextAction { get; set; } = null!;
        public string SuggestedChannel { get; set; } = null!;
        public string SuggestedMessage { get; set; } = null!;
        public bool AiPowered { get; set; }
    }

    public class MarketingCampaignBudgetDto
    {
        public Guid Id { get; set; }
        [Required, MaxLength(100)] public string Campaign { get; set; } = null!;
        [Range(0, double.MaxValue)] public decimal Spend { get; set; }
        [Required, StringLength(3)] public string Currency { get; set; } = "INR";
        [Range(0, double.MaxValue)] public decimal? WonRevenue { get; set; }
        public DateOnly StartDate { get; set; }
        public DateOnly EndDate { get; set; }
        public int Leads { get; set; }
        public int Converted { get; set; }
        public decimal? CostPerLead { get; set; }
        public decimal? CostPerAcquisition { get; set; }
        public decimal? ReturnOnAdSpend { get; set; }
    }

    public class MarketingReportSettingDto
    {
        public bool Enabled { get; set; }
        [Required, RegularExpression("^(Daily|Weekly)$")] public string Frequency { get; set; } = "Weekly";
        [Required, EmailAddress] public string RecipientEmail { get; set; } = null!;
        [Range(30, 730)] public int RetentionDays { get; set; } = 400;
        public DateTime? LastSentAtUtc { get; set; }
    }

    public class MarketingTeamMemberDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;
        public string Email { get; set; } = null!;
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

        /// <summary>Random id the browser keeps for this chat session, so its turns are saved as one conversation.</summary>
        [MaxLength(64)]
        [RegularExpression("^[A-Za-z0-9-]{8,64}$")]
        public string? ConversationId { get; set; }
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

    /// <summary>One website chat conversation, for the marketing console's list.</summary>
    public class MarketingChatConversationDto
    {
        public Guid Id { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime LastMessageAtUtc { get; set; }
        public int MessageCount { get; set; }
        public string FirstMessage { get; set; } = "";
        public string? Source { get; set; }
        public string? Campaign { get; set; }
        public string? LandingPath { get; set; }
        public string? BookedName { get; set; }
    }

    public class MarketingChatMessageDto
    {
        public string Role { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
    }

    public class MarketingChatDetailDto
    {
        public MarketingChatConversationDto Conversation { get; set; } = new();
        public List<MarketingChatMessageDto> Messages { get; set; } = new();
    }

    public class MarketingJourneyStepDto
    {
        public string Path { get; set; } = "";
        public DateTime AtUtc { get; set; }
    }

    /// <summary>One anonymous visitor's path through the site in the window.</summary>
    public class MarketingVisitorJourneyDto
    {
        /// <summary>Last 8 characters of the random browser id — enough to tell visitors apart, identifies no one.</summary>
        public string Visitor { get; set; } = "";
        /// <summary>The full random browser id, to open this visitor's own journey page.</summary>
        public string VisitorId { get; set; } = "";
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string? Country { get; set; }
        public string Device { get; set; } = "";
        public string Source { get; set; } = "";
        public string? Campaign { get; set; }
        public int PageViews { get; set; }
        public List<MarketingJourneyStepDto> Pages { get; set; } = new();
        public bool StartedChat { get; set; }
        public bool BookedDemo { get; set; }
        /// <summary>Other local days this visitor came back on within the window.</summary>
        public int ReturnVisits { get; set; }
    }

    public class MarketingVisitorJourneysDto
    {
        public int Days { get; set; }
        public string StartDate { get; set; } = "";
        public string EndDate { get; set; } = "";
        public int TotalVisitors { get; set; }
        public int SinglePageVisitors { get; set; }
        public int ReturningVisitors { get; set; }
        public int ChatVisitors { get; set; }
        public int BookedVisitors { get; set; }
        /// <summary>All countries in the window (unfiltered), for the country filter.</summary>
        public List<NamedCountDto> Countries { get; set; } = new();
        public List<NamedCountDto> EntryPages { get; set; } = new();
        public List<NamedCountDto> ExitPages { get; set; } = new();
        public List<NamedCountDto> TopJourneys { get; set; } = new();
        /// <summary>24 entries, "00"–"23" in the viewer's local time, counting visitors by arrival hour.</summary>
        public List<NamedCountDto> VisitsByHour { get; set; } = new();
        public List<MarketingVisitorJourneyDto> Visitors { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
    }

    /// <summary>How one blog post performed with real readers in the window.</summary>
    public class MarketingContentStatDto
    {
        public string Path { get; set; } = "";
        public string? Title { get; set; }
        public int Readers { get; set; }
        public int Views { get; set; }
        /// <summary>Readers who opened another page after this post.</summary>
        public int WentOn { get; set; }
        /// <summary>Readers who went on to /pricing, /get-started or /demo after this post.</summary>
        public int ToBuyingPages { get; set; }
        public int BookedDemo { get; set; }
    }

    public class MarketingContentPerformanceDto
    {
        public int Days { get; set; }
        public string StartDate { get; set; } = "";
        public string EndDate { get; set; } = "";
        public int BlogReaders { get; set; }
        public List<MarketingContentStatDto> Posts { get; set; } = new();
    }

    /// <summary>Everything the AI sees to suggest the next blog posts — aggregate stats and anonymised visitor questions only.</summary>
    public class MarketingBlogIdeaEvidence
    {
        public MarketingContentPerformanceDto Performance { get; set; } = new();
        /// <summary>Every existing post title (published, scheduled or draft), so ideas don't repeat them.</summary>
        public List<string> ExistingTitles { get; set; } = new();
        /// <summary>What visitors asked the website chat first, with phone numbers and emails removed.</summary>
        public List<string> VisitorQuestions { get; set; } = new();
    }

    public class MarketingBlogIdeaDto
    {
        public string Title { get; set; } = "";
        public string TargetKeyword { get; set; } = "";
        public string Why { get; set; } = "";
        public List<string> Outline { get; set; } = new();
    }

    public class MarketingBlogIdeasDto
    {
        public List<MarketingBlogIdeaDto> Ideas { get; set; } = new();
        public bool AiPowered { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }

    /// <summary>One visit: page views with no gap longer than 30 minutes.</summary>
    public class MarketingVisitDto
    {
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public string Source { get; set; } = "Direct";
        public string? Campaign { get; set; }
        public string Device { get; set; } = "";
        public List<MarketingJourneyStepDto> Pages { get; set; } = new();
        public bool StartedChat { get; set; }
    }

    public class MarketingVisitorLeadDto
    {
        public Guid Id { get; set; }
        public string FullName { get; set; } = "";
        public string AcademyName { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>Everything one anonymous visitor did, across all their visits (within the retention period).</summary>
    public class MarketingVisitorDetailDto
    {
        public string VisitorId { get; set; } = "";
        public string Visitor { get; set; } = "";
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string? Country { get; set; }
        public int PageViews { get; set; }
        public int DistinctPages { get; set; }
        public List<MarketingVisitDto> Visits { get; set; } = new();
        public List<MarketingVisitorLeadDto> DemoRequests { get; set; } = new();
        public List<MarketingChatConversationDto> Chats { get; set; } = new();
    }

    /// <summary>AI (or rule-based) read of one visitor: interests, buying intent, next marketing actions.</summary>
    public class MarketingVisitorInsightDto
    {
        public string Summary { get; set; } = "";
        public List<string> Interests { get; set; } = new();
        /// <summary>"Hot", "Warm" or "Cold".</summary>
        public string Intent { get; set; } = "Cold";
        public string IntentReason { get; set; } = "";
        public List<string> NextActions { get; set; } = new();
        public bool AiPowered { get; set; }
    }

    /// <summary>Ask the AI to write a full draft from an idea (usually one of the suggested ideas).</summary>
    public class MarketingBlogDraftRequest
    {
        [Required, MaxLength(160)] public string Title { get; set; } = "";
        [MaxLength(100)] public string? TargetKeyword { get; set; }
        [MaxLength(500)] public string? Why { get; set; }
        [MaxLength(8)] public List<string> Outline { get; set; } = new();
    }

    /// <summary>A full blog draft in the blog's own format, never published automatically.</summary>
    public class MarketingBlogDraftDto
    {
        public string Title { get; set; } = "";
        public string Excerpt { get; set; } = "";
        /// <summary>The blog's plain-text format: blank lines between paragraphs, "## " headings, "- " list lines, **bold**.</summary>
        public string Content { get; set; } = "";
        public bool AiPowered { get; set; }
    }
}
