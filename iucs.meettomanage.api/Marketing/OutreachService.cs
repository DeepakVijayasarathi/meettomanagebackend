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
    public const string KeyPrefix = "outreach.";
    public const int MaxDailyLimit = 500;

    /// <summary>Statuses that still receive e-mail; anything else stops the sequence.</summary>
    public static bool IsContactable(OutreachProspectStatus status) => status is OutreachProspectStatus.New or OutreachProspectStatus.Contacted;

    // ---------------------------------------------------------------- configuration

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

    /// <summary>
    /// Built-in defaults, overridden by the server's configuration (Outreach:From, Outreach:ReplyTo, ...), overridden in
    /// turn by what the marketing team saves in the console (outreach.* settings).
    /// </summary>
    private OutreachSettings Defaults()
    {
        var (name, address) = OutreachSettings.ParseFrom(configuration["Outreach:From"]);
        return new OutreachSettings
        {
            FromName = name ?? "Meet to Manage",
            FromAddress = address ?? "hello@meettomanage.cloud",
            ReplyTo = configuration["Outreach:ReplyTo"] is { Length: > 0 } r ? r : configuration["Marketing:LeadAlertEmail"],
            DailyLimit = configuration.GetValue("Outreach:DailyLimit", 50),
            WindowStartHour = configuration.GetValue("Outreach:WindowStartHour", 9),
            WindowEndHour = configuration.GetValue("Outreach:WindowEndHour", 19),
            TimeZoneId = configuration["Outreach:TimeZone"] is { Length: > 0 } tz ? tz : "Asia/Kolkata",
            FooterText = configuration["Outreach:FooterText"] is { Length: > 0 } f ? f
                : "You're receiving this because your academy's contact details are publicly listed. Not interested? Unsubscribe and we won't e-mail you again.",
            CompanyLine = configuration["Outreach:CompanyLine"] is { Length: > 0 } c ? c : "Meet to Manage · Infinity Uniquers, India",
            MinFollowUpDays = configuration.GetValue("Outreach:MinFollowUpDays", 2),
            MaxFailuresPerStep = configuration.GetValue("Outreach:MaxFailuresPerStep", 3),
        };
    }

    public async Task<OutreachSettings> GetSettingsAsync(CancellationToken ct)
    {
        var saved = await db.AppSettings.AsNoTracking().Where(x => x.Key.StartsWith(KeyPrefix)).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        return Defaults().Overlay(saved);
    }

    /// <summary>Validates and saves every outreach setting. Returns the first problem, or null when saved.</summary>
    public async Task<string?> SaveSettingsAsync(OutreachSettings settings, CancellationToken ct)
    {
        if (settings.Validate() is { } problem) return problem;
        var existing = await db.AppSettings.Where(x => x.Key.StartsWith(KeyPrefix)).ToDictionaryAsync(x => x.Key, ct);
        foreach (var (key, value) in settings.ToSettings())
        {
            if (!existing.TryGetValue(key, out var row))
                db.AppSettings.Add(row = new AppSetting { Key = key, Category = SettingCategory.General, IsPublic = false });
            row.Value = value;
        }
        await db.SaveChangesAsync(ct);
        return null;
    }

    public static DateTime StartOfTodayUtc(DateTime nowUtc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
    }

    public Task<int> SentTodayAsync(DateTime nowUtc, OutreachSettings settings, CancellationToken ct)
    {
        var start = StartOfTodayUtc(nowUtc, settings.Zone);
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

    private (string Html, string Text) Compose(string body, OutreachProspect p, OutreachSettings settings)
    {
        var rendered = Render(body, p);
        var url = UnsubscribeUrl(p.UnsubscribeToken);
        var footer = settings.FooterText;
        var company = WebUtility.HtmlEncode(settings.CompanyLine);
        var html = "<div style=\"font-family:Arial,sans-serif;font-size:15px;line-height:1.55;color:#1c1917\">" + ToHtml(rendered)
            + $"<p style=\"margin:24px 0 0;font-size:12px;color:#78716c\">{WebUtility.HtmlEncode(footer)} <a href=\"{url}\" style=\"color:#78716c\">Unsubscribe</a><br>{company}</p></div>";
        var text = $"{rendered.Trim()}\n\n--\n{footer}\nUnsubscribe: {url}\n{settings.CompanyLine}";
        return (html, text);
    }

    // ---------------------------------------------------------------- sending

    /// <summary>Sends one e-mail through Resend. Returns null on success, or the provider's error.</summary>
    private async Task<string?> SendAsync(OutreachProspect p, string subject, string body, OutreachSettings settings, CancellationToken ct)
    {
        var (html, text) = Compose(body, p, settings);
        var unsubscribe = UnsubscribeUrl(p.UnsubscribeToken);
        var client = http.CreateClient(nameof(OutreachService));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(new
            {
                from = settings.From,
                to = new[] { p.Email },
                reply_to = settings.ReplyTo is { Length: > 0 } r ? new[] { r } : null,
                subject = Render(subject, p),
                html,
                text,
                // One-click unsubscribe (RFC 8058): Gmail and Yahoo require it for bulk senders.
                headers = new Dictionary<string, string>
                {
                    ["List-Unsubscribe"] = $"<{unsubscribe}>",
                    ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
                },
            }, options: OmitNulls),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        using var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return null;
        var detail = await response.Content.ReadAsStringAsync(ct);
        return $"{(int)response.StatusCode}: {detail[..Math.Min(detail.Length, 300)]}";
    }

    /// <summary>Sends one human-approved reply and records it in the prospect conversation.</summary>
    public async Task<string?> SendReplyAsync(OutreachProspect prospect, string subject, string body, string? sentBy, CancellationToken ct)
    {
        if (NotReadyReason is { } reason) return reason;
        var error = await SendAsync(prospect, subject, body, await GetSettingsAsync(ct), ct);
        if (error is null)
        {
            prospect.LastContactedAtUtc = DateTime.UtcNow;
            prospect.NextActionAtUtc = null;
            db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = prospect.Id, Type = OutreachActivityType.ReplySent, Subject = subject, Detail = body, CreatedBy = sentBy });
            db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = prospect.Id, Type = OutreachActivityType.ReplyHandled, Detail = "Approved reply sent.", CreatedBy = sentBy });
            await db.SaveChangesAsync(ct);
        }
        return error;
    }

    /// <summary>Sends a reviewed one-off sales email from the lead workspace.</summary>
    public async Task<string?> SendApprovedMessageAsync(OutreachProspect prospect, string subject, string body, bool isReply, string? sentBy, CancellationToken ct)
    {
        if (NotReadyReason is { } reason) return reason;
        var error = await SendAsync(prospect, subject, body, await GetSettingsAsync(ct), ct);
        if (error is not null) return error;
        prospect.LastContactedAtUtc = DateTime.UtcNow;
        prospect.NextActionAtUtc = isReply ? null : DateTime.UtcNow.AddDays(3);
        if (!isReply)
        {
            prospect.Status = OutreachProspectStatus.Contacted;
            if (prospect.PipelineStage is OutreachPipelineStage.New or OutreachPipelineStage.Qualified) prospect.PipelineStage = OutreachPipelineStage.Contacted;
        }
        db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = prospect.Id, Type = isReply ? OutreachActivityType.ReplySent : OutreachActivityType.EmailSent, Subject = subject, Detail = body, CreatedBy = sentBy });
        if (isReply) db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = prospect.Id, Type = OutreachActivityType.ReplyHandled, Detail = "Approved reply sent.", CreatedBy = sentBy });
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Retries one failed campaign attempt without erasing the original audit record.</summary>
    public async Task<string?> RetryFailedSendAsync(Guid sendId, string? sentBy, CancellationToken ct)
    {
        var original = await db.OutreachSends.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sendId, ct);
        if (original is null) return "The failed send no longer exists.";
        if (original.Succeeded) return "Only failed sends can be retried.";
        if (await db.OutreachSends.AnyAsync(x => x.CampaignId == original.CampaignId && x.ProspectId == original.ProspectId && x.StepOrder == original.StepOrder && x.Succeeded && x.SentAtUtc > original.SentAtUtc, ct)) return "This email was already retried successfully.";
        var prospect = await db.OutreachProspects.FirstOrDefaultAsync(x => x.Id == original.ProspectId, ct);
        if (prospect is null) return "The prospect no longer exists.";
        if (prospect.Status is OutreachProspectStatus.Unsubscribed or OutreachProspectStatus.NotInterested) return "This prospect is no longer contactable.";
        var step = await db.OutreachCampaignSteps.AsNoTracking().FirstOrDefaultAsync(x => x.CampaignId == original.CampaignId && x.Order == original.StepOrder, ct);
        if (step is null) return "The original campaign step no longer exists.";
        if (NotReadyReason is { } reason) return reason;
        string? error;
        try { error = await SendAsync(prospect, step.Subject, step.Body, await GetSettingsAsync(ct), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
        var now = DateTime.UtcNow;
        db.OutreachSends.Add(new OutreachSend { CampaignId = original.CampaignId, ProspectId = original.ProspectId, StepOrder = original.StepOrder, Email = prospect.Email, Subject = Render(step.Subject, prospect), SentAtUtc = now, Succeeded = error is null, Error = error is null ? null : error[..Math.Min(error.Length, 500)] });
        if (error is null)
        {
            prospect.Status = OutreachProspectStatus.Contacted; prospect.PipelineStage = OutreachPipelineStage.Contacted; prospect.LastContactedAtUtc = now;
            db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = prospect.Id, Type = OutreachActivityType.EmailSent, Subject = Render(step.Subject, prospect), Detail = $"Campaign step {step.Order} sent successfully after manual retry.", CreatedBy = sentBy });
        }
        await db.SaveChangesAsync(ct);
        return error;
    }

    /// <summary>A test of one step to the marketing team's own address. Not counted towards the daily limit.</summary>
    public async Task<string?> SendTestAsync(Guid campaignId, int stepOrder, string to, CancellationToken ct)
    {
        if (NotReadyReason is { } reason) return reason;
        var step = await db.OutreachCampaignSteps.AsNoTracking().FirstOrDefaultAsync(x => x.CampaignId == campaignId && x.Order == stepOrder, ct);
        if (step is null) return "That step does not exist.";
        var sample = new OutreachProspect { Email = to.Trim(), FirstName = "Priya", AcademyName = "Bright Minds Academy", City = "Chennai", UnsubscribeToken = Guid.Empty };
        var error = await SendAsync(sample, "[Test] " + step.Subject, step.Body, await GetSettingsAsync(ct), ct);
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
        var settings = await GetSettingsAsync(ct);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, settings.Zone);
        if (local.Hour < settings.WindowStartHour || local.Hour >= settings.WindowEndHour) return false;

        var limit = settings.DailyLimit;
        if (await SentTodayAsync(nowUtc, settings, ct) >= limit) return false;
        var spacing = TimeSpan.FromMinutes((settings.WindowEndHour - settings.WindowStartHour) * 60.0 / limit);
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
        try { error = await SendAsync(recipient.Prospect, step.Subject, step.Body, settings, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }

        db.OutreachSends.Add(new OutreachSend
        {
            CampaignId = recipient.CampaignId, ProspectId = recipient.ProspectId, StepOrder = step.Order, Email = recipient.Prospect.Email,
            Subject = Render(step.Subject, recipient.Prospect), SentAtUtc = nowUtc, Succeeded = error is null, Error = error is null ? null : error[..Math.Min(error.Length, 500)],
        });

        if (error is null)
        {
            recipient.Prospect.Status = OutreachProspectStatus.Contacted;
            recipient.Prospect.PipelineStage = OutreachPipelineStage.Contacted;
            recipient.Prospect.LastContactedAtUtc = nowUtc;
            db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = recipient.ProspectId, Type = OutreachActivityType.EmailSent, Subject = Render(step.Subject, recipient.Prospect), Detail = $"Campaign step {step.Order} sent." });
            var next = steps.FirstOrDefault(s => s.Order > step.Order);
            if (next is null) recipient.Status = OutreachRecipientStatus.Completed;
            else { recipient.NextStep = next.Order; recipient.NextSendAtUtc = nowUtc.AddDays(Math.Max(settings.MinFollowUpDays, next.DelayDays)); }
        }
        else
        {
            var failures = await db.OutreachSends.CountAsync(x => x.CampaignId == recipient.CampaignId && x.ProspectId == recipient.ProspectId && x.StepOrder == step.Order && !x.Succeeded, ct) + 1;
            // An address the provider rejects outright (422) or that keeps failing is treated as bounced.
            if (error.StartsWith("422") || failures >= settings.MaxFailuresPerStep)
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

    // No reply-to means the field is left out entirely rather than sent as null.
    private static readonly System.Text.Json.JsonSerializerOptions OmitNulls = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

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
