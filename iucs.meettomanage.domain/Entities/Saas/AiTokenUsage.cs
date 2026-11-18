namespace iucs.meettomanage.domain.Entities.Saas;

/// <summary>
/// AI tokens used on the platform's AI key, one row per tenant, calendar month (UTC, "yyyy-MM") and feature.
/// The platform's own marketing site is kept apart under <see cref="MarketingTenantId"/>, so it is never counted
/// against an academy.
/// </summary>
public class AiTokenUsage
{
    /// <summary>The marketing site's usage. Not a real tenant.</summary>
    public static readonly Guid MarketingTenantId = Guid.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Month { get; set; } = "";
    /// <summary>What used the tokens, e.g. mito, chatbot, copilot, site-chat.</summary>
    public string Feature { get; set; } = "";
    public long Requests { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
