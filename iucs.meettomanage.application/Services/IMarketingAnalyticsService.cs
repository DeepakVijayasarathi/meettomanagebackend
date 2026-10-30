using iucs.meettomanage.application.Dto.Marketing;

namespace iucs.meettomanage.application.Services
{
    /// <summary>First-party, anonymous analytics for the public marketing site and its dashboard.</summary>
    public interface IMarketingAnalyticsService
    {
        /// <summary>Records one event; silently drops bots and malformed paths.</summary>
        Task TrackAsync(TrackMarketingEventRequest request, string? userAgent, CancellationToken cancellationToken = default);

        /// <summary>
        /// Dashboard for the last <paramref name="days"/> days (clamped 1–90), with calendar days in
        /// the caller's UTC offset (<paramref name="tzOffsetMinutes"/>, e.g. 330 for IST).
        /// </summary>
        /// <summary>The last <paramref name="days"/> local days including today, or — when both are given —
        /// the local calendar days <paramref name="from"/> to <paramref name="to"/> inclusive.</summary>
        Task<MarketingDashboardDto> GetDashboardAsync(int days, int tzOffsetMinutes, CancellationToken cancellationToken = default, DateOnly? from = null, DateOnly? to = null);

        /// <summary>Each blog post's readers in the window, and how many went on to another page or a buying page.</summary>
        Task<MarketingContentPerformanceDto> GetContentPerformanceAsync(int days, int tzOffsetMinutes,
            CancellationToken cancellationToken = default, DateOnly? from = null, DateOnly? to = null);

        /// <summary>Evidence for AI blog suggestions: content performance, every existing title, and recent visitor chat questions (contact details removed).</summary>
        Task<MarketingBlogIdeaEvidence> GetBlogIdeaEvidenceAsync(int days, int tzOffsetMinutes,
            CancellationToken cancellationToken = default, DateOnly? from = null, DateOnly? to = null);

        /// <summary>Per-visitor journeys (pages in order) for the same window, plus entry/exit pages, common paths and visit hours.</summary>
        Task<MarketingVisitorJourneysDto> GetVisitorJourneysAsync(int days, int tzOffsetMinutes, int page, int pageSize, string? country = null,
            CancellationToken cancellationToken = default, DateOnly? from = null, DateOnly? to = null);
    }
}
