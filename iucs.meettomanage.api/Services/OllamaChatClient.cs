using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using iucs.meettomanage.application.Common.Interfaces;

namespace iucs.meettomanage.api.Services
{
    /// <summary>Adapter for Ollama's /api/chat JSON contract (also supported by many local AI gateways).</summary>
    public class OllamaChatClient : IAiChatClient
    {
        private readonly IHttpClientFactory _clients;
        public OllamaChatClient(IHttpClientFactory clients) => _clients = clients;

        public async Task<string> CompleteAsync(string endpoint, string? bearerKey, string model,
            IReadOnlyList<AiChatTurn> messages, int maxTokens, double temperature, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new
                {
                    model,
                    messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                    stream = false,
                    options = new { num_predict = maxTokens, temperature },
                }),
            };
            if (!string.IsNullOrWhiteSpace(bearerKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerKey);
            using var response = await _clients.CreateClient(nameof(OllamaChatClient)).SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"AI provider returned {(int)response.StatusCode}.");
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || string.IsNullOrWhiteSpace(content.GetString()))
                throw new InvalidOperationException("AI provider returned no message content.");
            return content.GetString()!.Trim();
        }
    }
}
