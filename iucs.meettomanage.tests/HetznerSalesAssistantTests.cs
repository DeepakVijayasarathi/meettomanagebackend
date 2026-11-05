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

    [Fact]
    public async Task Marketing_analysis_returns_structured_hetzner_recommendations()
    {
        const string body = """{"choices":[{"message":{"content":"{\"summary\":\"Traffic is growing.\",\"observations\":[\"Chat is strong.\"],\"nextActions\":[\"Improve the demo CTA.\"]}"}}]}""";
        var result = await Create(new FakeHandler((HttpStatusCode.OK, body)), new() { ApiKey = "key", Model = "primary" })
            .AnalyzeMarketingAsync(new MarketingDashboardDto { Days = 30, Totals = new() { Visitors = 32, DemoRequests = 3 } });

        Assert.True(result.AiPowered);
        Assert.Equal("Traffic is growing.", result.Summary);
        Assert.Contains("Improve the demo CTA.", result.NextActions);
    }

    [Fact]
    public async Task Marketing_analysis_has_measured_fallback_without_api_key()
    {
        var result = await Create(new FakeHandler((HttpStatusCode.OK, OkBody)), new())
            .AnalyzeMarketingAsync(new MarketingDashboardDto { Days = 7, Totals = new() { Visitors = 10, DemoRequests = 1, ConversionRate = 10 } });

        Assert.False(result.AiPowered);
        Assert.NotEmpty(result.NextActions);
    }

    [Fact]
    public async Task Marketing_analysis_rejects_visitor_counts_above_authoritative_total()
    {
        const string body = """{"choices":[{"message":{"content":"{\"summary\":\"282 visitors arrived.\",\"observations\":[\"Traffic increased.\"],\"nextActions\":[\"Verify tracking.\"]}"}}]}""";
        var handler = new FakeHandler((HttpStatusCode.OK, body));
        var result = await Create(handler, new() { ApiKey = "key", Model = "primary" })
            .AnalyzeMarketingAsync(new MarketingDashboardDto { Days = 30, Totals = new() { Visitors = 110, PageViews = 282 } });

        Assert.False(result.AiPowered);
        Assert.Contains("110 visitors", result.Summary);
        Assert.Contains("dailyVisitorsAreUniqueWithinEachDayAndMustNeverBeSummed", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Marketing_fallback_suggests_tracking_and_attribution_checks_before_experiments()
    {
        var result = await Create(new FakeHandler((HttpStatusCode.OK, OkBody)), new()).AnalyzeMarketingAsync(new MarketingDashboardDto
        {
            Days = 30,
            Totals = new() { Visitors = 110, PageViews = 700, DemoRequests = 0, ChatsStarted = 18, ChatStartRate = 16.4 },
            Sources = [new() { Name = "Direct", Count = 107 }, new() { Name = "Google", Count = 3 }],
            TopPages = [new() { Name = "/pricing", Count = 69 }, new() { Name = "/get-started", Count = 37 }],
        });

        Assert.Contains(result.NextActions, x => x.Contains("test booking", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.NextActions, x => x.Contains("UTM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.NextActions, x => x.Contains("After tracking is verified", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.NextActions, x => x.Contains("A/B test", StringComparison.OrdinalIgnoreCase) && !x.Contains("before", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Marketing_fallback_distinguishes_submit_persistence_gap_from_no_conversion()
    {
        var result = await Create(new FakeHandler((HttpStatusCode.OK, OkBody)), new()).AnalyzeMarketingAsync(new MarketingDashboardDto
        {
            Days = 7,
            Totals = new() { Visitors = 17, FormStartedVisitors = 3, FormSubmittedVisitors = 2, DemoRequests = 0 },
            Sources = [new() { Name = "Direct", Count = 17 }],
        });

        Assert.Contains(result.NextActions, x => x.Contains("persistence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Observations, x => x.Contains("2 form submitter", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.NextActions, x => x.Contains("UTM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Marketing_fallback_does_not_recommend_legal_page_as_conversion_page()
    {
        var result = await Create(new FakeHandler((HttpStatusCode.OK, OkBody)), new()).AnalyzeMarketingAsync(new MarketingDashboardDto
        {
            Days = 7,
            Totals = new() { Visitors = 17 },
            TopPages = [new() { Name = "/privacy-policy", Count = 10 }, new() { Name = "/", Count = 7 }],
        });

        Assert.DoesNotContain(result.NextActions, x => x.Contains("privacy-policy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.NextActions, x => x.Contains("highest-traffic conversion-eligible page", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Site_improvements_do_not_invent_causes_for_tiny_samples()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, OkBody));
        var result = await Create(handler, new() { ApiKey = "key", Model = "primary" })
            .SuggestSiteImprovementsAsync(new MarketingSiteBehaviourDto { Visitors = 2, Visits = 2, SinglePageVisits = 2 });

        Assert.False(result.AiPowered);
        Assert.Contains("not enough evidence", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Behaviour, x => x.Contains("do not mean an immediate exit", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Improvements, x => Assert.Equal("Low", x.Impact));
        Assert.Empty(handler.Requests);
    }

    private static HetznerSalesAssistant Create(FakeHandler handler, HetznerAiOptions options) =>
        new(new FakeHttpClientFactory(handler), new FakeDemoRequests(), Microsoft.Extensions.Options.Options.Create(options), NullLogger<HetznerSalesAssistant>.Instance);

    private sealed class FakeDemoRequests : IPlatformDemoRequestService
    {
        public Task<PlatformDemoRequestDto> CreateAsync(CreatePlatformDemoRequestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlatformDemoRequestDto>> ListAsync(StoreInquiryStatus? status, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlatformDemoRequestDto> UpdateStatusAsync(Guid id, UpdatePlatformDemoRequestStatusRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlatformDemoRequestDto> UpdateAsync(Guid id, UpdatePlatformDemoRequestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<iucs.meettomanage.application.Dto.Common.PagedResult<PlatformDemoRequestDto>> ListPagedAsync(MarketingLeadQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MarketingLeadDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task BulkUpdateAsync(BulkMarketingLeadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MarketingLeadActivityDto> AddActivityAsync(Guid id, CreateMarketingLeadActivityRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
