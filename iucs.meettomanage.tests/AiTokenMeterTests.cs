using System.Net;
using System.Text;
using iucs.meettomanage.api.Saas;
using iucs.meettomanage.api.Services;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Common.Options;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests;

/// <summary>An in-memory master database with an AI token meter whose plan limits are set per tenant.</summary>
internal sealed class AiMasterDb : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    public DbContextOptions<MasterDbContext> Options { get; }

    public AiMasterDb()
    {
        _connection.Open();
        Options = new DbContextOptionsBuilder<MasterDbContext>().UseSqlite(_connection).Options;
        using var db = new MasterDbContext(Options);
        db.Database.EnsureCreated();
    }

    public AiTokenMeter Meter(IReadOnlyDictionary<Guid, long> limits) =>
        new(new Factory(Options), new Limits(limits), NullLogger<AiTokenMeter>.Instance);

    public void Dispose() => _connection.Dispose();

    private sealed class Factory(DbContextOptions<MasterDbContext> options) : IDbContextFactory<MasterDbContext>
    {
        public MasterDbContext CreateDbContext() => new(options);
    }

    /// <summary>A managed plan with the given AI limit; a tenant without one is on a plan that sets no AI limit.</summary>
    private sealed class Limits(IReadOnlyDictionary<Guid, long> limits) : IEntitlementService
    {
        public Task<TenantEntitlements> GetAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(new TenantEntitlements(true, "plan", "Plan", SaasSubscriptionStatus.Active,
                limits.TryGetValue(tenantId, out var limit)
                    ? new Dictionary<string, long> { [EntitlementKeys.MaxAiTokensPerMonth] = limit }
                    : new Dictionary<string, long>()));
        public void Invalidate(Guid tenantId) { }
        public void InvalidateAll() { }
    }
}

public sealed class AiTokenMeterTests : IDisposable
{
    private readonly AiMasterDb _master = new();
    private static readonly Guid AcademyA = Guid.NewGuid();
    private static readonly Guid AcademyB = Guid.NewGuid();
    private static string ThisMonth => AiTokenMeter.MonthOf(DateTime.UtcNow);

    public void Dispose() => _master.Dispose();

    [Fact]
    public async Task Usage_is_counted_per_academy_and_feature_and_kept_apart_from_marketing()
    {
        var meter = _master.Meter(new Dictionary<Guid, long>());
        await meter.RecordAsync(AcademyA, AiUsageScope.Mito, 100, 20);
        await meter.RecordAsync(AcademyA, AiUsageScope.Mito, 50, 10);
        await meter.RecordAsync(AcademyA, AiUsageScope.Chatbot, 5, 5);
        await meter.RecordAsync(AcademyB, AiUsageScope.Mito, 7, 3);
        await meter.RecordAsync(AiTokenUsage.MarketingTenantId, AiUsageScope.SiteChat, 1000, 500);

        var mito = Assert.Single(await meter.FeaturesAsync(AcademyA, ThisMonth), x => x.Feature == AiUsageScope.Mito);
        Assert.Equal((2L, 150L, 30L), (mito.Requests, mito.PromptTokens, mito.CompletionTokens));
        Assert.Equal(190, (await meter.AllowanceAsync(AcademyA)).UsedTokens);
        Assert.Equal(10, (await meter.AllowanceAsync(AcademyB)).UsedTokens);
        Assert.Equal(4, (await meter.MonthAsync(ThisMonth)).Count); // A: mito + chatbot, B: mito, marketing: site-chat
    }

    [Fact]
    public async Task Concurrent_calls_never_lose_a_count()
    {
        var meter = _master.Meter(new Dictionary<Guid, long>());
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => meter.RecordAsync(AcademyA, AiUsageScope.Mito, 10, 0)));
        var row = Assert.Single(await meter.FeaturesAsync(AcademyA, ThisMonth));
        Assert.Equal((20L, 200L), (row.Requests, row.PromptTokens));
    }

    [Fact]
    public async Task The_plan_allowance_is_enforced_and_an_unset_limit_is_unlimited()
    {
        var meter = _master.Meter(new Dictionary<Guid, long> { [AcademyA] = 100, [AcademyB] = 0 });
        await meter.EnsureWithinAllowanceAsync(AcademyA);
        await meter.RecordAsync(AcademyA, AiUsageScope.Mito, 80, 20);

        var full = await meter.AllowanceAsync(AcademyA);
        Assert.True(full.Exhausted);
        Assert.Equal(0, full.Remaining);
        var ex = await Assert.ThrowsAsync<PlanLimitException>(() => meter.EnsureWithinAllowanceAsync(AcademyA));
        Assert.Contains("this month's AI allowance", ex.Message);
        Assert.Contains("aren't included", (await Assert.ThrowsAsync<PlanLimitException>(() => meter.EnsureWithinAllowanceAsync(AcademyB))).Message);

        var other = Guid.NewGuid();
        await meter.RecordAsync(other, AiUsageScope.Mito, 1_000_000, 0);
        Assert.True((await meter.AllowanceAsync(other)).Unlimited);
        await meter.EnsureWithinAllowanceAsync(other);
    }

    [Theory]
    [InlineData("2026-11", true)]
    [InlineData("2026-13", false)]
    [InlineData("2026-1", false)]
    [InlineData("nov", false)]
    public void Months_are_validated(string month, bool valid) => Assert.Equal(valid, AiTokenMeter.IsValidMonth(month));

    // ------------------------------------------------------------------ the academies' metered client

    private const string Reply = """{"choices":[{"message":{"content":"Here you go."}}],"usage":{"prompt_tokens":40,"completion_tokens":12}}""";

    private (MeteredAiChatClient Client, StubHandler Handler, AiTokenMeter Meter) Metered(Guid tenantId, long? limit = null)
    {
        var handler = new StubHandler(Reply);
        var options = Microsoft.Extensions.Options.Options.Create(new HetznerAiOptions
        {
            Enabled = true, ApiKey = "platform-key", Endpoint = "https://inference.hetzner.com/v1", Model = "platform-model",
        });
        var meter = _master.Meter(limit is { } l ? new Dictionary<Guid, long> { [tenantId] = l } : new Dictionary<Guid, long>());
        var tenant = new TenantContext();
        tenant.Bind(tenantId, "academy", "Host=unused;Database=mt_test");
        return (new MeteredAiChatClient(new OllamaChatClient(new StubFactory(handler), options), options, tenant, meter), handler, meter);
    }

    [Fact]
    public async Task An_academy_always_uses_the_platform_key_and_its_tokens_are_metered()
    {
        var (client, handler, meter) = Metered(AcademyA);
        using (AiUsageScope.Enter(AiUsageScope.Copilot))
            await client.CompleteAsync("https://academy-own.example/v1", "academy-own-key", "academy-model", [new AiChatTurn("user", "hi")], 300, 0.3);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://inference.hetzner.com/v1/chat/completions", sent.Url);
        Assert.Equal("platform-key", sent.Key);
        Assert.Contains("\"platform-model\"", sent.Body);
        var row = Assert.Single(await meter.FeaturesAsync(AcademyA, ThisMonth));
        Assert.Equal((AiUsageScope.Copilot, 40L, 12L), (row.Feature, row.PromptTokens, row.CompletionTokens));
        Assert.Equal(["platform-model"], await client.ListModelsAsync("https://academy-own.example/v1", "academy-own-key"));
    }

    [Fact]
    public async Task An_exhausted_allowance_never_reaches_the_provider()
    {
        var (client, handler, meter) = Metered(AcademyA, limit: 50);
        await meter.RecordAsync(AcademyA, AiUsageScope.Mito, 50, 0);
        await Assert.ThrowsAsync<PlanLimitException>(() => client.CompleteAsync("", null, "", [new AiChatTurn("user", "hi")], 300, 0.3));
        Assert.Empty(handler.Requests);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public List<(string Url, string? Key, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.Parameter,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}

public sealed class ChatbotPlatformProviderTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private sealed class Client(bool platform) : IAiChatClient
    {
        public int Calls { get; private set; }
        public bool UsesPlatformProvider => platform;
        public Task<AiChatCompletion> CompleteAsync(string endpoint, string? bearerKey, string model, IReadOnlyList<AiChatTurn> messages, int maxTokens, double temperature, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new AiChatCompletion("Answer.", 10, 5)); }
        public Task<IReadOnlyList<string>> ListModelsAsync(string endpoint, string? bearerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private iucs.meettomanage.application.Services.ChatbotService Service(IAiChatClient client) => new(_db.UnitOfWork, client,
        new iucs.meettomanage.application.Helper.AesGcmPinVault(Microsoft.Extensions.Options.Options.Create(new iucs.meettomanage.application.Helper.PinVaultOptions { Key = "test-key-test-key-test-key-test-key" })),
        Microsoft.Extensions.Options.Options.Create(new HetznerAiOptions { Endpoint = "https://inference.hetzner.com/v1", Model = "platform-model" }));

    [Fact]
    public async Task A_single_client_install_still_has_to_save_its_own_endpoint()
    {
        var client = new Client(platform: false);
        await Assert.ThrowsAsync<DomainValidationException>(() => Service(client).TestAiAsync(new() { Message = "hi" }));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task An_academy_on_the_platform_key_needs_no_saved_endpoint()
    {
        var client = new Client(platform: true);
        Assert.Equal("Answer.", (await Service(client).TestAiAsync(new() { Message = "hi" })).Answer);
        Assert.Equal(1, client.Calls);
    }
}
