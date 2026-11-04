using System.Net;
using System.Text.Json;
using iucs.meettomanage.api.Marketing;
using iucs.meettomanage.domain.Entities.Marketing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class OutreachTests : IDisposable
{
    // 10:30 IST, inside the 9:00-19:00 sending window.
    private static readonly DateTime Morning = new(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc);
    private readonly TestDatabase _db = new();
    private readonly FakeResend _resend = new();

    private sealed class FakeResend : HttpMessageHandler, IHttpClientFactory
    {
        public List<JsonDocument> Sent { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Status) { Content = new StringContent(Status == HttpStatusCode.OK ? "{\"id\":\"x\"}" : "{\"message\":\"invalid\"}") };
        }
    }

    private OutreachService Service(string? apiKey = "re_test", string? apiBase = "https://api.test.dev") =>
        new(_db.Context, _resend, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resend:ApiKey"] = apiKey, ["Api:BaseUrl"] = apiBase, ["Marketing:LeadAlertEmail"] = "team@test.dev",
        }).Build(), NullLogger<OutreachService>.Instance);

    private async Task<(OutreachCampaign Campaign, OutreachProspect Prospect)> SeedAsync(OutreachProspectStatus status = OutreachProspectStatus.New)
    {
        var prospect = new OutreachProspect { Email = "owner@academy.in", FirstName = "Anita", AcademyName = "Sunrise Abacus", Status = status };
        var campaign = new OutreachCampaign { Name = "Abacus centres", Status = OutreachCampaignStatus.Active };
        campaign.Steps.Add(new OutreachCampaignStep { Order = 1, DelayDays = 0, Subject = "Hi {{FirstName}}", Body = "Running {{AcademyName}} in {{City}}?" });
        campaign.Steps.Add(new OutreachCampaignStep { Order = 2, DelayDays = 3, Subject = "Following up", Body = "Any thoughts, {{FirstName}}?" });
        _db.Context.OutreachProspects.Add(prospect);
        _db.Context.OutreachCampaigns.Add(campaign);
        await _db.Context.SaveChangesAsync();
        _db.Context.OutreachCampaignRecipients.Add(new OutreachCampaignRecipient { CampaignId = campaign.Id, ProspectId = prospect.Id, NextSendAtUtc = Morning.AddMinutes(-1) });
        await _db.Context.SaveChangesAsync();
        return (campaign, prospect);
    }

    [Fact]
    public void MergeFieldsFallBackToFriendlyWordsAndHtmlIsEscaped()
    {
        var p = new OutreachProspect { Email = "a@b.in", FirstName = " ", AcademyName = "Bright <Kids>" };
        Assert.Equal("Hi there from Bright <Kids> in your city", OutreachService.Render("Hi {{FirstName}} from {{ academyname }} in {{City}}", p));
        Assert.Equal("<p style=\"margin:0 0 14px\">a &lt;b&gt;<br>c</p><p style=\"margin:0 0 14px\">see <a href=\"https://x.in\">https://x.in</a></p>",
            OutreachService.ToHtml("a <b>\nc\n\nsee https://x.in"));
    }

    [Fact]
    public void SendingStaysOffWithoutAProviderOrARealPublicAddress()
    {
        Assert.NotNull(Service(apiKey: null).NotReadyReason);
        Assert.NotNull(Service(apiBase: "https://api.example.com").NotReadyReason);
        Assert.NotNull(Service(apiBase: null).NotReadyReason);
        Assert.Null(Service().NotReadyReason);
    }

    [Fact]
    public async Task SendsTheFirstStepWithUnsubscribeAndSchedulesTheFollowUp()
    {
        var (_, prospect) = await SeedAsync();
        Assert.True(await Service().ProcessDueAsync(Morning, CancellationToken.None));

        var mail = Assert.Single(_resend.Sent).RootElement;
        Assert.Equal("Hi Anita", mail.GetProperty("subject").GetString());
        Assert.Contains("Sunrise Abacus in your city", mail.GetProperty("text").GetString());
        var unsubscribe = $"https://api.test.dev/api/marketing/outreach/unsubscribe/{prospect.UnsubscribeToken:N}";
        Assert.Equal($"<{unsubscribe}>", mail.GetProperty("headers").GetProperty("List-Unsubscribe").GetString());
        Assert.Contains(unsubscribe, mail.GetProperty("html").GetString());
        Assert.Equal("team@test.dev", mail.GetProperty("reply_to")[0].GetString());

        var recipient = await _db.Context.OutreachCampaignRecipients.AsNoTracking().SingleAsync();
        Assert.Equal(2, recipient.NextStep);
        Assert.Equal(Morning.AddDays(3), recipient.NextSendAtUtc);
        Assert.Equal(OutreachProspectStatus.Contacted, (await _db.Context.OutreachProspects.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task SpacesSendsAndRespectsTheDailyLimit()
    {
        await SeedAsync();
        var svc = Service();
        Assert.Null(await svc.SaveSettingsAsync((await svc.GetSettingsAsync(CancellationToken.None)) with { DailyLimit = 1 }, CancellationToken.None));
        Assert.True(await svc.ProcessDueAsync(Morning, CancellationToken.None));
        // a second prospect is due, but today's single send is used up
        var other = new OutreachProspect { Email = "two@academy.in" };
        _db.Context.OutreachProspects.Add(other);
        await _db.Context.SaveChangesAsync();
        var campaignId = (await _db.Context.OutreachCampaigns.SingleAsync()).Id;
        _db.Context.OutreachCampaignRecipients.Add(new OutreachCampaignRecipient { CampaignId = campaignId, ProspectId = other.Id, NextSendAtUtc = Morning });
        await _db.Context.SaveChangesAsync();
        Assert.False(await svc.ProcessDueAsync(Morning.AddHours(2), CancellationToken.None));
        Assert.Single(_resend.Sent);
    }

    [Fact]
    public async Task StopsACompetingCampaignInsideTheGlobalCooldown()
    {
        var (firstCampaign, prospect) = await SeedAsync();
        var svc = Service();
        Assert.True(await svc.ProcessDueAsync(Morning, CancellationToken.None));

        var competing = new OutreachCampaign { Name = "Competing campaign", Status = OutreachCampaignStatus.Active };
        competing.Steps.Add(new OutreachCampaignStep { Order = 1, DelayDays = 0, Subject = "Another email", Body = "This must not send." });
        _db.Context.OutreachCampaigns.Add(competing);
        await _db.Context.SaveChangesAsync();
        _db.Context.OutreachCampaignRecipients.Add(new OutreachCampaignRecipient
        {
            CampaignId = competing.Id, ProspectId = prospect.Id, NextSendAtUtc = Morning.AddHours(2)
        });
        await _db.Context.SaveChangesAsync();

        Assert.False(await svc.ProcessDueAsync(Morning.AddHours(2), CancellationToken.None));
        Assert.Single(_resend.Sent);
        var stopped = await _db.Context.OutreachCampaignRecipients.AsNoTracking().SingleAsync(x => x.CampaignId == competing.Id);
        Assert.Equal(OutreachRecipientStatus.Stopped, stopped.Status);
        Assert.NotEqual(firstCampaign.Id, competing.Id);
    }

    [Fact]
    public async Task NeverSendsOutsideSendingHours()
    {
        await SeedAsync();
        Assert.False(await Service().ProcessDueAsync(new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc), CancellationToken.None)); // 20:30 IST
        Assert.Empty(_resend.Sent);
    }

    [Fact]
    public async Task UnsubscribeOrAReplyStopsTheSequence()
    {
        var (_, prospect) = await SeedAsync();
        Assert.True(await Service().UnsubscribeAsync(prospect.UnsubscribeToken, CancellationToken.None));
        Assert.False(await Service().ProcessDueAsync(Morning, CancellationToken.None));
        Assert.Empty(_resend.Sent);
        _db.Context.ChangeTracker.Clear();
        Assert.Equal(OutreachRecipientStatus.Stopped, (await _db.Context.OutreachCampaignRecipients.AsNoTracking().SingleAsync()).Status);
        Assert.False(await Service().UnsubscribeAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ARejectedAddressIsMarkedBounced()
    {
        await SeedAsync();
        _resend.Status = HttpStatusCode.UnprocessableEntity;
        Assert.True(await Service().ProcessDueAsync(Morning, CancellationToken.None));
        _db.Context.ChangeTracker.Clear();
        Assert.Equal(OutreachProspectStatus.Bounced, (await _db.Context.OutreachProspects.AsNoTracking().SingleAsync()).Status);
        Assert.False((await _db.Context.OutreachSends.AsNoTracking().SingleAsync()).Succeeded);
    }

    [Fact]
    public async Task SettingsDefaultFromServerConfigAndSavedValuesWin()
    {
        var svc = Service();
        var defaults = await svc.GetSettingsAsync(CancellationToken.None);
        Assert.Equal("Meet to Manage <hello@meettomanage.cloud>", defaults.From);
        Assert.Equal("team@test.dev", defaults.ReplyTo);
        Assert.Equal(50, defaults.DailyLimit);

        var custom = defaults with
        {
            FromName = "Deepak from Meet to Manage", FromAddress = "deepak@meettomanage.cloud", ReplyTo = "",
            WindowStartHour = 10, WindowEndHour = 17, TimeZoneId = "Asia/Dubai", FooterText = "We found you on Justdial.", CompanyLine = "Infinity Uniquers, Chennai 600001",
        };
        Assert.Null(await svc.SaveSettingsAsync(custom, CancellationToken.None));
        var saved = await svc.GetSettingsAsync(CancellationToken.None);
        Assert.Equal("Deepak from Meet to Manage <deepak@meettomanage.cloud>", saved.From);
        Assert.Null(saved.ReplyTo); // cleared on purpose
        Assert.Equal("Asia/Dubai", saved.TimeZoneId);

        // Sending hours follow the saved time zone: 05:00 UTC is 09:00 in Dubai, before the 10:00 start...
        await SeedAsync();
        Assert.False(await svc.ProcessDueAsync(Morning, CancellationToken.None));
        // ...and 06:30 UTC is 10:30 there, inside it. The saved footer, company line and sender are used.
        Assert.True(await svc.ProcessDueAsync(Morning.AddMinutes(90), CancellationToken.None));
        var mail = Assert.Single(_resend.Sent).RootElement;
        Assert.Equal("Deepak from Meet to Manage <deepak@meettomanage.cloud>", mail.GetProperty("from").GetString());
        Assert.Contains("We found you on Justdial.", mail.GetProperty("text").GetString());
        Assert.Contains("Infinity Uniquers, Chennai 600001", mail.GetProperty("html").GetString());
        Assert.False(mail.TryGetProperty("reply_to", out _)); // omitted, not null
    }

    [Theory]
    [InlineData("fromAddress", "not-an-email")]
    [InlineData("window", "end-before-start")]
    [InlineData("timeZone", "Mars/Olympus")]
    [InlineData("dailyLimit", "0")]
    public async Task InvalidSettingsAreRefusedWithAReason(string field, string value)
    {
        var svc = Service();
        var s = await svc.GetSettingsAsync(CancellationToken.None);
        s = field switch
        {
            "fromAddress" => s with { FromAddress = value },
            "window" => s with { WindowStartHour = 18, WindowEndHour = 9 },
            "timeZone" => s with { TimeZoneId = value },
            _ => s with { DailyLimit = 0 },
        };
        Assert.NotNull(await svc.SaveSettingsAsync(s, CancellationToken.None));
    }

    public void Dispose() => _db.Dispose();
}
