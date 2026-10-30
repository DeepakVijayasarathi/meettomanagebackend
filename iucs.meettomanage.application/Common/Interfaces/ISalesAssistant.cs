using iucs.meettomanage.application.Dto.Marketing;

namespace iucs.meettomanage.application.Common.Interfaces
{
    /// <summary>
    /// The marketing site's chat assistant: answers a prospective academy's questions about the
    /// platform and can book their free platform demo itself, from details collected in the chat.
    /// The API-layer implementation calls Hetzner Inference; it throws
    /// <see cref="Exceptions.ExternalServiceException"/> when the model is unconfigured or
    /// unreachable so the site can fall back to its direct contact options.
    /// </summary>
    public interface ISalesAssistant
    {
        Task<SalesChatResponse> ReplyAsync(IReadOnlyList<SalesChatTurn> conversation, CancellationToken cancellationToken = default, MarketingLeadAttributionDto? attribution = null);
        Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken cancellationToken = default);
        Task<MarketingAiInsightsDto> AnalyzeMarketingAsync(MarketingDashboardDto dashboard, CancellationToken cancellationToken = default);
        Task<MarketingLeadRecommendationDto> RecommendLeadAsync(PlatformDemoRequestDto lead, CancellationToken cancellationToken = default);
    }
}
