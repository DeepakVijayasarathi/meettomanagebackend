namespace iucs.meettomanage.application.Common.Interfaces
{
    public record AiChatTurn(string Role, string Content);
    public record AiChatCompletion(string Content, int PromptTokens, int CompletionTokens);

    /// <summary>Calls an OpenAI/Ollama-compatible chat endpoint. Implemented in the API layer.</summary>
    public interface IAiChatClient
    {
        Task<AiChatCompletion> CompleteAsync(
            string endpoint,
            string? bearerKey,
            string model,
            IReadOnlyList<AiChatTurn> messages,
            int maxTokens,
            double temperature,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> ListModelsAsync(
            string endpoint,
            string? bearerKey,
            CancellationToken cancellationToken = default);
    }
}
