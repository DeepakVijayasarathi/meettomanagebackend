using iucs.meettomanage.application.Dto.Marketing;

namespace iucs.meettomanage.application.Common.Interfaces
{
    /// <summary>
    /// The marketing site's chat assistant: answers a prospective academy's questions about the
    /// platform and steers them toward booking a demo. The API-layer implementation calls
    /// Gemini; it throws <see cref="Exceptions.ExternalServiceException"/> when the model is
    /// unconfigured or unreachable so the site can fall back to its direct contact options.
    /// </summary>
    public interface ISalesAssistant
    {
        Task<string> ReplyAsync(IReadOnlyList<SalesChatTurn> conversation, CancellationToken cancellationToken = default);
    }
}
