using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Entities.Settings;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Marketing;

/// <summary>
/// Cold outreach for the marketing team: renders a campaign step for a prospect, sends it through the platform's
/// Resend account with an unsubscribe link and one-click unsubscribe header, and works through due recipients within
/// the daily limit, spaced across sending hours. Prospects who replied, unsubscribed or bounced are never e-mailed again.
/// </summary>
public sealed class OutreachService(MeetToManageDbContext db, IHttpClientFactory http, IConfiguration configuration, ILogger<OutreachService> logger)
{
    public const string DailyLimitKey = "outreach.dailyLimit";
    public const int DefaultDailyLimit = 50;
    public const int MaxDailyLimit = 500;
    /// <summary>Sending hours (IST). Sends are spread evenly across this window instead of all at once.</summary>
    public const int WindowStartHour = 9, WindowEndHour = 19;
    private const int MaxFailuresPerStep = 3;
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    /// <summary>Statuses that still receive e-mail; anything else stops the sequence.</summary>
    public static bool IsContactable(OutreachProspectStatus status) => status is OutreachProspectStatus.New or OutreachProspectStatus.Contacted;

    // ---------------------------------------------------------------- configuration

    public string From => configuration["Outreach:From"] is { Length: > 0 } f ? f : "Meet to Manage <hello@meettomanage.cloud>";
    public string? ReplyTo => configuration["Outreach:ReplyTo"] is { Length: > 0 } r ? r : configuration["Marketing:LeadAlertEmail"];
    private string? ApiKey => configuration["Resend:ApiKey"] is { Length: > 0 } k ? k : null;

    /// <summary>Public base for unsubscribe links; a missing or placeholder value blocks sending rather than mailing broken links.</summary>
    public string? UnsubscribeBase
    {
        get
        {
            var value = (configuration["Outreach:UnsubscribeBaseUrl"] ?? configuration["Api:BaseUrl"])?.Trim().TrimEnd('/');
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && !uri.Host.EndsWith("example.com", StringComparison.OrdinalIgnoreCase) && uri.Host != "localhost" ? value : null;
        }
    }

    /// <summary>Why sending cannot run, or null when it can.</summary>
    public string? NotReadyReason =>
        ApiKey is null ? "No e-mail provider is configured (Resend:ApiKey)."
        : UnsubscribeBase is null ? "The public API address (Api:BaseUrl or Outreach:UnsubscribeBaseUrl) is missing, so unsubscribe links cannot be built."
        : null;

    public async Task<int> GetDailyLimitAsync(CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking().Where(x => x.Key == DailyLimitKey).Select(x => x.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out var v) && v is > 0 and <= MaxDailyLimit ? v : DefaultDailyLimit;
    }

    public async Task SetDailyLimitAsync(int limit, CancellationToken ct)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == DailyLimitKey, ct);
        if (row is null) db.AppSettings.Add(row = new AppSetting { Key = DailyLimitKey, Category = SettingCategory.General, IsPublic = false });
        row.Value = Math.Clamp(limit, 1, MaxDailyLimit).ToString();
        await db.SaveChangesAsync(ct);
    }

    public static DateTime StartOfTodayUtc(DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, Ist).Date;
        return TimeZoneInfo.ConvertTimeToUtc(local, Ist);
    }

    public Task<int> SentTodayAsync(DateTime nowUtc, CancellationToken ct)
    {
        var start = StartOfTodayUtc(nowUtc);
        return db.OutreachSends.CountAsync(x => x.Succeeded && x.SentAtUtc >= start && x.ProspectId != Guid.Empty, ct);
    }

    // ---------------------------------------------------------------- rendering

    private static readonly Regex Field = new(@"\{\{\s*(FirstName|AcademyName|City)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Fills {{FirstName}}, {{AcademyName}} and {{City}}, with friendly fallbacks for blanks.</summary>
    public static string Render(string template, OutreachProspect p) => Field.Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
    {
        "firstname" => string.IsNullOrWhiteSpace(p.FirstName) ? "there" : p.FirstName.Trim(),
        "academyname" => string.IsNullOrWhiteSpace(p.AcademyName) ? "your academy" : p.AcademyName.Trim(),
        _ => string.IsNullOrWhiteSpace(p.City) ? "your city" : p.City.Trim(),
    });

    /// <summary>Plain-text body to simple HTML: escaped, paragraphs on blank lines, line breaks kept, links clickable.</summary>
    public static string ToHtml(string text)
    {
        var paragraphs = Regex.Split(text.Replace("\r\n", "\n").Trim(), @"\n\s*\n")
            .Select(p => WebUtility.HtmlEncode(p).Replace("\n", "<br>"))
            .Select(p => Regex.Replace(p, @"(https?://[^\s<]+)", "<a href=\"$1\">$1</a>"));
        return string.Concat(paragraphs.Select(p => $"<p style=\"margin:0 0 14px\">{p}</p>"));
    }

    public string UnsubscribeUrl(Guid token) => $"{UnsubscribeBase}/api/marketing/outreach/unsubscribe/{token:N}";

    private (string Html, string Text) Compose(string body, OutreachProspect p)
    {
        var rendered = Render(body, p);
        var url = UnsubscribeUrl(p.UnsubscribeToken);
        var footer = "You're receiving this because your academy's contact details are publicly listed. Not interested? Unsubscribe and we won't e-mail you again.";
        var html = "<div style=\"font-family:Arial,sans-serif;font-size:15px;line-height:1.55;color:#1c1917\">" + ToHtml(rendered)
            + $"<p style=\"margin:24px 0 0;font-size:12px;color:#78716c\">{WebUtility.HtmlEncode(footer)} <a href=\"{url}\" style=\"color:#78716c\">Unsubscribe</a><br>Meet to Manage · Infinity Uniquers, India</p></div>";
        var text = $"{rendered.Trim()}\n\n--\n{footer}\nUnsubscribe: {url}\nMeet to Manage · Infinity Uniquers, India";
        return (html, text);
    }

    // ---------------------------------------------------------------- sending

    /// <summary>Sends one e-mail through Resend. Returns null on success, or the provider's error.</summary>
    private async Task<string?> SendAsync(OutreachProspect p, string subject, string body, CancellationToken ct)
    {
        var (html, text) = Compose(body, p);
        var unsubscribe = UnsubscribeUrl(p.UnsubscribeToken);
        var client = http.CreateClient(nameof(OutreachService));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(new
            {
                from = From,
                to = new[] { p.Email },
                reply_to = ReplyTo is { Length: > 0 } r ? new[] { r } : null,
                subject = Render(subject, p),
                html,
                text,
                // One-click unsubscribe (RFC 8058): Gmail and Yahoo require it for bulk senders.
                headers = new Dictionary<string, string>
                {
                    ["List-Unsubscribe"] = $"<{unsubscribe}>",
                    ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
                },
            }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        using var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return null;
        var detail = await response.Content.ReadAsStringAsync(ct);
        return $"{(int)response.StatusCode}: {detail[..Math.Min(detail.Length, 300)]}";
    }

    /// <summary>A test of one step to the marketing team's own address. Not counted towards the daily limit.</summary>
    public async Task<string?> SendTestAsync(Guid campaignId, int stepOrder, string to, CancellationToken ct)
    {
        if (NotReadyReason is { } reason) return reason;
        var step = await db.OutreachCampaignSteps.AsNoTracking().FirstOrDefaultAsync(x => x.CampaignId == campaignId && x.Order == stepOrder, ct);
        if (step is null) return "That step does not exist.";
        var sample = new OutreachProspect { Email = to.Trim(), FirstName = "Priya", AcademyName = "Bright Minds Academy", City = "Chennai", UnsubscribeToken = Guid.Empty };
        var error = await SendAsync(sample, "[Test] " + step.Subject, step.Body, ct);
        db.OutreachSends.Add(new OutreachSend { CampaignId = campaignId, ProspectId = Guid.Empty, StepOrder = stepOrder, Email = sample.Email, Subject = "[Test] " + step.Subject, Succeeded = error is null, Error = error });
        await db.SaveChangesAsync(ct);
        return error;
    }

    /// <summary>
    /// One worker tick: stops sequences for prospects who are no longer contactable, completes finished campaigns, and
    /// sends at most one due e-mail if the daily limit and the even spacing across sending hours allow it.
    /// Returns true when an e-mail was attempted.
    /// </summary>
    public async Task<bool> ProcessDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        // Housekeeping first, so a reply or unsubscribe always wins over a scheduled send.
        await db.OutreachCampaignRecipients
            .Where(r => r.Status == OutreachRecipientStatus.Scheduled && !(r.Prospect.Status == OutreachProspectStatus.New || r.Prospect.Status == OutreachProspectStatus.Contacted))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, OutreachRecipientStatus.Stopped), ct);
        await db.OutreachCampaigns
            .Where(c => c.Status == OutreachCampaignStatus.Active && c.Recipients.Any() && !c.Recipients.Any(r => r.Status == OutreachRecipientStatus.Scheduled))
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, OutreachCampaignStatus.Completed), ct);

        if (NotReadyReason is not null) return false;
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, Ist);
        if (local.Hour < WindowStartHour || local.Hour >= WindowEndHour) return false;

        var limit = await GetDailyLimitAsync(ct);
        if (await SentTodayAsync(nowUtc, ct) >= limit) return false;
        var spacing = TimeSpan.FromMinutes((WindowEndHour - WindowStartHour) * 60.0 / limit);
        var last = await db.OutreachSends.Where(x => x.ProspectId != Guid.Empty).OrderByDescending(x => x.SentAtUtc).Select(x => (DateTime?)x.SentAtUtc).FirstOrDefaultAsync(ct);
        if (last is { } l && nowUtc - l < spacing) return false;

        var recipient = await db.OutreachCampaignRecipients
            .Include(r => r.Prospect)
            .Where(r => r.Status == OutreachRecipientStatus.Scheduled && r.NextSendAtUtc <= nowUtc && r.Campaign.Status == OutreachCampaignStatus.Active
                && (r.Prospect.Status == OutreachProspectStatus.New || r.Prospect.Status == OutreachProspectStatus.Contacted))
            .OrderBy(r => r.NextSendAtUtc)
            .FirstOrDefaultAsync(ct);
        if (recipient is null) return false;

        var steps = await db.OutreachCampaignSteps.AsNoTracking().Where(s => s.CampaignId == recipient.CampaignId).OrderBy(s => s.Order).ToListAsync(ct);
        var step = steps.FirstOrDefault(s => s.Order == recipient.NextStep);
        if (step is null)
        {
            recipient.Status = OutreachRecipientStatus.Completed;
            await db.SaveChangesAsync(ct);
            return false;
        }

        string? error;
        try { error = await SendAsync(recipient.Prospect, step.Subject, step.Body, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }

        db.OutreachSends.Add(new OutreachSend
        {
            CampaignId = recipient.CampaignId, ProspectId = recipient.ProspectId, StepOrder = step.Order, Email = recipient.Prospect.Email,
            Subject = Render(step.Subject, recipient.Prospect), SentAtUtc = nowUtc, Succeeded = error is null, Error = error is null ? null : error[..Math.Min(error.Length, 500)],
        });

        if (error is null)
        {
            recipient.Prospect.Status = OutreachProspectStatus.Contacted;
            recipient.Prospect.LastContactedAtUtc = nowUtc;
            var next = steps.FirstOrDefault(s => s.Order > step.Order);
            if (next is null) recipient.Status = OutreachRecipientStatus.Completed;
            else { recipient.NextStep = next.Order; recipient.NextSendAtUtc = nowUtc.AddDays(Math.Max(1, next.DelayDays)); }
        }
        else
        {
            var failures = await db.OutreachSends.CountAsync(x => x.CampaignId == recipient.CampaignId && x.ProspectId == recipient.ProspectId && x.StepOrder == step.Order && !x.Succeeded, ct) + 1;
            // An address the provider rejects outright (422) or that keeps failing is treated as bounced.
            if (error.StartsWith("422") || failures >= MaxFailuresPerStep)
            {
                recipient.Status = OutreachRecipientStatus.Stopped;
                recipient.Prospect.Status = OutreachProspectStatus.Bounced;
            }
            else recipient.NextSendAtUtc = nowUtc.AddHours(1);
            logger.LogWarning("Outreach e-mail to prospect {ProspectId} failed: {Error}", recipient.ProspectId, error);
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Unsubscribes the prospect with this link token. Returns false for an unknown token.</summary>
    public async Task<bool> UnsubscribeAsync(Guid token, CancellationToken ct)
    {
        if (token == Guid.Empty) return false;
        var prospect = await db.OutreachProspects.FirstOrDefaultAsync(x => x.UnsubscribeToken == token, ct);
        if (prospect is null) return false;
        if (prospect.Status != OutreachProspectStatus.Unsubscribed)
        {
            prospect.Status = OutreachProspectStatus.Unsubscribed;
            prospect.UnsubscribedAtUtc = DateTime.UtcNow;
            await db.OutreachCampaignRecipients.Where(r => r.ProspectId == prospect.Id && r.Status == OutreachRecipientStatus.Scheduled)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, OutreachRecipientStatus.Stopped), ct);
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    public static bool LooksLikeEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && Regex.IsMatch(value.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
}

/// <summary>Works through due outreach e-mails, one tick a minute, per tenant (only the marketing tenant has any).</summary>
public sealed class OutreachBackgroundService(IServiceScopeFactory scopes, ILogger<OutreachBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MeetToManageDbContext>();
                // Cheap check so every other tenant's worker does nothing.
                if (await db.OutreachCampaigns.AnyAsync(c => c.Status == OutreachCampaignStatus.Active, stoppingToken))
                    await scope.ServiceProvider.GetRequiredService<OutreachService>().ProcessDueAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outreach cycle failed; retrying next minute.");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
