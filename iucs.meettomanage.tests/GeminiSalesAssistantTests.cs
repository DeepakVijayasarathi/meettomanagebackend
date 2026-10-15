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

        Assert.Equal("About ₹4,000 a month.", reply.Reply);
        Assert.Null(reply.BookedName);
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

        Assert.Equal("About ₹4,000 a month.", (await assistant.ReplyAsync(Conversation)).Reply);
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

        Assert.Contains("info@infinityuniquers.com", (await assistant.ReplyAsync(Conversation)).Reply);
    }

    private static string FunctionCallBody(string args) => $$$"""
        {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"book_demo","id":"call_1","args":{{{args}}}},"thoughtSignature":"sig"}]}}]}
        """;

    private const string ValidArgs = """{"full_name":"Priya Kapoor","phone":"+91 98765 43210","email":"priya@brightstep.in","organization":"Bright Step Academy","notes":"Phonics academy, 120 students"}""";

    private const string BookedBody = """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"All set, Priya! The team will WhatsApp you within a day."}]}}]}
        """;

    [Fact]
    public async Task Confirmed_booking_is_saved_and_the_model_words_the_reply()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, FunctionCallBody(ValidArgs)), (HttpStatusCode.OK, BookedBody));
        var demos = new FakeDemoRequests();
        var assistant = Create(handler, new GeminiOptions { ApiKey = "k", Model = "primary", FallbackModel = "fallback" }, demos);

        var reply = await assistant.ReplyAsync(Conversation);

        Assert.Equal("Priya Kapoor", reply.BookedName);
        Assert.StartsWith("All set, Priya!", reply.Reply);
        var saved = Assert.Single(demos.Created);
        Assert.Equal(("Priya Kapoor", "+91 98765 43210", "priya@brightstep.in", "Bright Step Academy"), (saved.FullName, saved.Phone, saved.WorkEmail, saved.AcademyName));
        Assert.Contains("Phonics academy, 120 students", saved.Message);

        // The follow-up call echoes the model's call (with its signature) and answers it by id.
        using var followUp = JsonDocument.Parse(handler.Requests[1].Body);
        var contents = followUp.RootElement.GetProperty("contents").EnumerateArray().ToList();
        Assert.Equal("sig", contents[^2].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
        var functionResponse = contents[^1].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("call_1", functionResponse.GetProperty("id").GetString());
        Assert.True(functionResponse.GetProperty("response").GetProperty("booked").GetBoolean());
    }

    [Theory]
    [InlineData("""{"full_name":"Priya","phone":"+91 98765 43210","email":"priya-at-gmail"}""", "email")]
    [InlineData("""{"full_name":"Priya","phone":"12","email":"priya@example.com"}""", "phone")]
    [InlineData("""{"full_name":"","phone":"+91 98765 43210","email":"priya@example.com"}""", "name")]
    public async Task Invalid_details_are_not_booked_and_go_back_to_the_model(string args, string problem)
    {
        var handler = new FakeHandler((HttpStatusCode.OK, FunctionCallBody(args)), (HttpStatusCode.OK, BookedBody));
        var demos = new FakeDemoRequests();
        var assistant = Create(handler, new GeminiOptions { ApiKey = "k" }, demos);

        var reply = await assistant.ReplyAsync(Conversation);

        Assert.Null(reply.BookedName);
        Assert.Empty(demos.Created);
        using var followUp = JsonDocument.Parse(handler.Requests[1].Body);
        var result = followUp.RootElement.GetProperty("contents").EnumerateArray().Last().GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("response");
        Assert.False(result.GetProperty("booked").GetBoolean());
        Assert.Contains(problem, result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_second_book_call_in_one_turn_does_not_book_twice()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, FunctionCallBody(ValidArgs)), (HttpStatusCode.OK, FunctionCallBody(ValidArgs)), (HttpStatusCode.OK, BookedBody));
        var demos = new FakeDemoRequests();
        var assistant = Create(handler, new GeminiOptions { ApiKey = "k" }, demos);

        var reply = await assistant.ReplyAsync(Conversation);

        Assert.Single(demos.Created);
        Assert.Equal("Priya Kapoor", reply.BookedName);
        Assert.Equal(3, handler.Requests.Count);
    }

    private static GeminiSalesAssistant Create(FakeHandler handler, GeminiOptions options, FakeDemoRequests? demos = null) =>
        new(new FakeHttpClientFactory(handler), demos ?? new FakeDemoRequests(), Microsoft.Extensions.Options.Options.Create(options), NullLogger<GeminiSalesAssistant>.Instance);

    private sealed class FakeDemoRequests : IPlatformDemoRequestService
    {
        public List<CreatePlatformDemoRequestRequest> Created { get; } = new();

        public Task<PlatformDemoRequestDto> CreateAsync(CreatePlatformDemoRequestRequest request, CancellationToken cancellationToken = default)
        {
            Created.Add(request);
            return Task.FromResult(new PlatformDemoRequestDto { Id = Guid.NewGuid(), FullName = request.FullName, WorkEmail = request.WorkEmail, Phone = request.Phone, AcademyName = request.AcademyName });
        }

        public Task<IReadOnlyList<PlatformDemoRequestDto>> ListAsync(StoreInquiryStatus? status, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PlatformDemoRequestDto> UpdateStatusAsync(Guid id, UpdatePlatformDemoRequestStatusRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

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
