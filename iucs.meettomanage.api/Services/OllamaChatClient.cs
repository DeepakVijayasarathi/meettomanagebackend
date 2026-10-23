using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using iucs.meettomanage.application.Common.Interfaces;

namespace iucs.meettomanage.api.Services
{
    /// <summary>Adapter for both Ollama /api/chat and OpenAI-compatible /chat/completions providers.</summary>
    public class OllamaChatClient : IAiChatClient
    {
        private readonly IHttpClientFactory _clients;
        public OllamaChatClient(IHttpClientFactory clients) => _clients = clients;

        public async Task<string> CompleteAsync(string endpoint, string? bearerKey, string model,
            IReadOnlyList<AiChatTurn> messages, int maxTokens, double temperature, CancellationToken cancellationToken = default)
        {
            var openAi = IsOpenAi(endpoint);
            var chatUrl = openAi ? Join(endpoint, "chat/completions") : endpoint;
            // Hetzner's current Qwen reasoning models count their hidden reasoning against
            // max_tokens. With the old 120-token default they often stopped with finish_reason
            // "length" before emitting message.content at all. Preserve small limits for Ollama,
            // but guarantee enough room for an OpenAI-compatible reasoning response.
            var effectiveMaxTokens = openAi ? Math.Max(maxTokens, 500) : maxTokens;
            object payload = openAi
                // Qwen reasoning models can otherwise spend the whole output budget on hidden
                // reasoning and return an empty message.content (or take minutes on a grounded
                // business prompt). Hetzner's OpenAI-compatible endpoint forwards this Qwen
                // chat-template option, producing the concise visible answer the UI needs.
                ? new
                {
                    model,
                    messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                    stream = false,
                    max_tokens = effectiveMaxTokens,
                    temperature,
                    chat_template_kwargs = new { enable_thinking = false },
                }
                : new { model, messages = messages.Select(m => new { role = m.Role, content = m.Content }), stream = false, options = new { num_predict = maxTokens, temperature } };
            using var request = new HttpRequestMessage(HttpMethod.Post, chatUrl)
            {
                Content = JsonContent.Create(payload),
            };
            if (!string.IsNullOrWhiteSpace(bearerKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerKey);
            using var response = await _clients.CreateClient(nameof(OllamaChatClient)).SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"AI provider returned {(int)response.StatusCode}.");
            using var json = JsonDocument.Parse(body);
            JsonElement content = default;
            var found = openAi
                ? json.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("message", out var openAiMessage) && openAiMessage.TryGetProperty("content", out content)
                : json.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("content", out content);
            if (!found || string.IsNullOrWhiteSpace(content.GetString()))
                throw new InvalidOperationException("AI provider returned no message content.");
            return content.GetString()!.Trim();
        }

        public async Task<IReadOnlyList<string>> ListModelsAsync(string endpoint, string? bearerKey, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Join(endpoint, "models"));
            if (!string.IsNullOrWhiteSpace(bearerKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerKey);
            using var response = await _clients.CreateClient(nameof(OllamaChatClient)).SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"AI provider returned {(int)response.StatusCode} while listing models.");
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
            return data.EnumerateArray().Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().OrderBy(x => x).ToList();
        }

        private static bool IsOpenAi(string endpoint) => endpoint.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || endpoint.Contains("chat/completions", StringComparison.OrdinalIgnoreCase);
        private static string Join(string endpoint, string path)
        {
            var value = endpoint.TrimEnd('/');
            if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                value = value[..^"/chat/completions".Length];
            return $"{value}/{path}";
        }
    }
}
