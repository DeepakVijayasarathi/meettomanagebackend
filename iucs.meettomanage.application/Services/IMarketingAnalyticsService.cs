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
        Task<MarketingDashboardDto> GetDashboardAsync(int days, int tzOffsetMinutes, CancellationToken cancellationToken = default);
    }
}
