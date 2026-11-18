using iucs.meettomanage.api.Saas;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Common.Options;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Services;

/// <summary>
/// The AI client academies use in multi-tenant mode. Every call goes to the platform's own provider (HetznerAi
/// endpoint, key and model); an endpoint or key an academy saved is ignored, so its usage cannot leave the meter.
/// Each call is refused once the academy's monthly token allowance is used up, and its tokens are recorded against
/// that academy in the master database.
/// </summary>
public sealed class MeteredAiChatClient(OllamaChatClient inner, IOptions<HetznerAiOptions> options, TenantContext tenant, AiTokenMeter meter) : IAiChatClient
{
    private readonly HetznerAiOptions _options = options.Value;

    private Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("AI calls need a bound academy.");

    public async Task<AiChatCompletion> CompleteAsync(string endpoint, string? bearerKey, string model,
        IReadOnlyList<AiChatTurn> messages, int maxTokens, double temperature, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        if (!_options.IsConfigured) throw new InvalidOperationException("AI is not set up on this platform.");
        await meter.EnsureWithinAllowanceAsync(tenantId, cancellationToken);
        var completion = await inner.CompleteAsync(_options.Endpoint, _options.ApiKey, _options.Model, messages, maxTokens, temperature, cancellationToken);
        await meter.RecordAsync(tenantId, AiUsageScope.Current, completion.PromptTokens, completion.CompletionTokens);
        return completion;
    }

    /// <summary>Academies don't choose a model on the platform's key; the platform model is the only one offered.</summary>
    public Task<IReadOnlyList<string>> ListModelsAsync(string endpoint, string? bearerKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(string.IsNullOrWhiteSpace(_options.Model) ? [] : [_options.Model]);
}
