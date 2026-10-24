using System.Net;
using System.Text;
using System.Text.Json;
using iucs.meettomanage.api.Services;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Options;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests;

public class HetznerSalesAssistantTests
{
    private static readonly SalesChatTurn[] Conversation = [new() { Role = "user", Text = "Hi" }];
    private const string OkBody = """{"choices":[{"message":{"role":"assistant","content":"Hello from Hetzner AI."}}]}""";

    [Fact]
    public async Task Uses_hetzner_openai_endpoint_and_bearer_key()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, OkBody));
        var assistant = Create(handler, new() { ApiKey = "test-key", Endpoint = "https://inference.hetzner.com/api/v1", Model = "primary" });
        Assert.Equal("Hello from Hetzner AI.", (await assistant.ReplyAsync(Conversation)).Reply);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://inference.hetzner.com/api/v1/chat/completions", sent.Url);
        Assert.Equal("test-key", sent.ApiKey);
        using var payload = JsonDocument.Parse(sent.Body);
        Assert.Equal("primary", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("system", payload.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Missing_key_never_calls_provider()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, OkBody));
        await Assert.ThrowsAsync<ExternalServiceException>(() => Create(handler, new()).ReplyAsync(Conversation));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Retryable_failure_uses_fallback_model()
    {
        var handler = new FakeHandler((HttpStatusCode.ServiceUnavailable, "{}"), (HttpStatusCode.OK, OkBody));
        await Create(handler, new() { ApiKey = "key", Model = "primary", FallbackModel = "fallback" }).ReplyAsync(Conversation);
        using var payload = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("fallback", payload.RootElement.GetProperty("model").GetString());
    }

    private static HetznerSalesAssistant Create(FakeHandler handler, HetznerAiOptions options) =>
        new(new FakeHttpClientFactory(handler), new FakeDemoRequests(), Microsoft.Extensions.Options.Options.Create(options), NullLogger<HetznerSalesAssistant>.Instance);

    private sealed class FakeDemoRequests : IPlatformDemoRequestService
    {
        public Task<PlatformDemoRequestDto> CreateAsync(CreatePlatformDemoRequestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlatformDemoRequestDto>> ListAsync(StoreInquiryStatus? status, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlatformDemoRequestDto> UpdateStatusAsync(Guid id, UpdatePlatformDemoRequestStatusRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public List<(string Url, string? ApiKey, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.Parameter, body));
            var (status, responseBody) = responses[Math.Min(Requests.Count, responses.Length) - 1];
            return new(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
