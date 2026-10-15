using System.Net;
using System.Text;
using System.Text.Json;
using iucs.meettomanage.api.Services;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Options;
using iucs.meettomanage.application.Dto.Marketing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests;

public class GeminiSalesAssistantTests
{
    private static readonly SalesChatTurn[] Conversation =
    [
        new() { Role = "user", Text = "Hi" },
        new() { Role = "assistant", Text = "Hello! How many students?" },
        new() { Role = "user", Text = "About 50" },
    ];

    private const string OkBody = """
        {"candidates":[{"content":{"parts":[{"text":"thinking...","thought":true},{"text":"About "},{"text":"₹4,000 a month."}]},"finishReason":"STOP"}]}
        """;

    [Fact]
    public async Task Sends_key_and_conversation_and_returns_reply_text_without_thoughts()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, OkBody));
        var assistant = Create(handler, new GeminiOptions { ApiKey = "test-key", Model = "primary", FallbackModel = "fallback" });

        var reply = await assistant.ReplyAsync(Conversation);

        Assert.Equal("About ₹4,000 a month.", reply);
        var sent = Assert.Single(handler.Requests);
        Assert.Contains("/models/primary:generateContent", sent.Url);
        Assert.Equal("test-key", sent.ApiKey);
        using var payload = JsonDocument.Parse(sent.Body);
        var roles = payload.RootElement.GetProperty("contents").EnumerateArray().Select(c => c.GetProperty("role").GetString());
        Assert.Equal(["user", "model", "user"], roles);
        Assert.Contains("Meet to Manage", payload.RootElement.GetProperty("systemInstruction").GetRawText());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Overloaded_primary_model_falls_back_once(HttpStatusCode status)
    {
        var handler = new FakeHandler((status, "{}"), (HttpStatusCode.OK, OkBody));
        var assistant = Create(handler, new GeminiOptions { ApiKey = "k", Model = "primary", FallbackModel = "fallback" });

        Assert.Equal("About ₹4,000 a month.", await assistant.ReplyAsync(Conversation));
        Assert.Contains("/models/fallback:", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Rejected_request_does_not_retry_and_surfaces_a_friendly_error()
    {
        var handler = new FakeHandler((HttpStatusCode.BadRequest, "{}"), (HttpStatusCode.OK, OkBody));
        var assistant = Create(handler, new GeminiOptions { ApiKey = "k", Model = "primary", FallbackModel = "fallback" });

        await Assert.ThrowsAsync<ExternalServiceException>(() => assistant.ReplyAsync(Conversation));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Missing_api_key_never_calls_gemini()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, OkBody));
        var assistant = Create(handler, new GeminiOptions { ApiKey = "" });

        await Assert.ThrowsAsync<ExternalServiceException>(() => assistant.ReplyAsync(Conversation));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Blocked_or_empty_answer_returns_a_polite_fallback()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, """{"candidates":[{"finishReason":"SAFETY"}]}"""));
        var assistant = Create(handler, new GeminiOptions { ApiKey = "k" });

        Assert.Contains("info@infinityuniquers.com", await assistant.ReplyAsync(Conversation));
    }

    private static GeminiSalesAssistant Create(FakeHandler handler, GeminiOptions options) =>
        new(new FakeHttpClientFactory(handler), Microsoft.Extensions.Options.Options.Create(options), NullLogger<GeminiSalesAssistant>.Instance);

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public List<(string Url, string? ApiKey, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var key = request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null;
            Requests.Add((request.RequestUri!.ToString(), key, body));
            var (status, responseBody) = responses[Math.Min(Requests.Count, responses.Length) - 1];
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
