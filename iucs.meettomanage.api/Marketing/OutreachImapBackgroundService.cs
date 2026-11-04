using System.Net.Http.Json;
using System.Text.RegularExpressions;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Marketing;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Marketing;

/// <summary>
/// Pulls unread messages from a dedicated Zoho mailbox and forwards them through the same
/// authenticated inbound-reply endpoint used by webhook providers. This keeps reply processing
/// idempotent and tenant-aware while requiring no paid automation service.
/// </summary>
public sealed class OutreachImapBackgroundService(
    IHttpClientFactory clients,
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<OutreachImapBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim syncLock = new(1, 1);
    private readonly object stateLock = new();
    private OutreachImapSyncStatus state = new(false, false, null, null, null, 0, 0, 0, 0, 0);

    public OutreachImapSyncStatus Status
    {
        get
        {
            lock (stateLock)
            {
                return state.LastAttemptAtUtc is null ? state with { Configured = IsConfigured() } : state;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PollAfterDelay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncNowAsync(stoppingToken, "Automatic"); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Zoho outreach mailbox poll failed; retrying later.");
            }
            await PollAfterDelay(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Outreach:Imap:PollSeconds", 60), 30, 900)), stoppingToken);
        }
    }

    public async Task<OutreachImapSyncStatus> SyncNowAsync(CancellationToken ct, string trigger = "Manual")
    {
        if (!await syncLock.WaitAsync(0, ct)) return Status;
        var runId = Guid.Empty;
        SetState(Status with { IsRunning = true, LastAttemptAtUtc = DateTime.UtcNow, LastError = null });
        try
        {
        runId = await StartRunAsync(trigger, ct);
        var username = configuration["Outreach:Imap:Username"];
        var password = configuration["Outreach:Imap:Password"];
        var secret = configuration["Outreach:InboundWebhookSecret"];
        var apiBase = configuration["Api:BaseUrl"]?.TrimEnd('/');
        var configured = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password) && !string.IsNullOrWhiteSpace(secret) && !string.IsNullOrWhiteSpace(apiBase);
        if (!configured)
        {
            SetState(Status with { Configured = false, IsRunning = false, LastError = "IMAP reply sync is not fully configured." });
            await FinishRunAsync(runId, Status, [], ct);
            return Status;
        }

        var host = configuration["Outreach:Imap:Host"] ?? "imap.zoho.in";
        var port = configuration.GetValue("Outreach:Imap:Port", 993);
        using var imap = new ImapClient();
        await imap.ConnectAsync(host, port, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(username!, password!, ct);
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        var unread = await inbox.SearchAsync(SearchQuery.NotSeen, ct);
        var http = clients.CreateClient(nameof(OutreachImapBackgroundService));
        http.DefaultRequestHeaders.Add("X-Outreach-Webhook-Secret", secret);

        var checkedCount = 0;
        var importedCount = 0;
        var duplicateCount = 0;
        var unmatchedCount = 0;
        var failedCount = 0;
        var unmatched = new List<UnmatchedMessage>();
        foreach (var uid in unread.Take(50))
        {
            checkedCount++;
            var message = await inbox.GetMessageAsync(uid, ct);
            var from = message.From.Mailboxes.FirstOrDefault()?.Address;
            if (string.IsNullOrWhiteSpace(from))
            {
                unmatchedCount++;
                await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
                continue;
            }
            var body = message.TextBody;
            if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(message.HtmlBody))
                body = HtmlText.PlainTextFromHtml(message.HtmlBody);
            var threadReferences = Limit(string.Join(' ', message.InReplyTo, string.Join(' ', message.References)), 500);
            var linkedProspectId = await FindProspectByThreadAsync(message.InReplyTo, message.References, message.Subject, ct);
            var payload = new
            {
                from = Limit(from, 256),
                subject = Limit(message.Subject, 200),
                text = Limit(CleanReply(body ?? "(No readable message body)"), 10000),
                externalId = Limit(!string.IsNullOrWhiteSpace(message.MessageId) ? $"zoho:{message.MessageId}" : $"zoho-uid:{inbox.UidValidity}:{uid.Id}", 200),
                prospectId = linkedProspectId
            };
            using var response = await http.PostAsJsonAsync($"{apiBase}/api/marketing/outreach/inbound/reply", payload, ct);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<InboundResult>(cancellationToken: ct);
                await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
                if (result?.Duplicate == true) duplicateCount++;
                else if (result?.Matched == true) importedCount++;
                else
                {
                    unmatchedCount++;
                    unmatched.Add(new(payload.externalId!, payload.from!, payload.subject, payload.text!, threadReferences));
                }
            }
            else
            {
                failedCount++;
                logger.LogWarning("Reply webhook rejected Zoho message {MessageId} with HTTP {Status}; leaving it unread for retry.", payload.externalId, (int)response.StatusCode);
            }
        }
        await imap.DisconnectAsync(true, ct);
        SetState(new(true, false, DateTime.UtcNow, DateTime.UtcNow,
            failedCount == 0 ? null : $"{failedCount} message(s) could not be imported and remain unread for retry.",
            checkedCount, importedCount, duplicateCount, unmatchedCount, failedCount));
        await FinishRunAsync(runId, Status, unmatched, ct);
        return Status;
        }
        catch (OperationCanceledException)
        {
            SetState(Status with { IsRunning = false });
            await FinishRunAsync(runId, Status with { LastError = "Mailbox sync was cancelled." }, [], CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Zoho outreach mailbox sync failed.");
            SetState(Status with { IsRunning = false, LastError = "Mailbox sync failed. Check IMAP settings and the app password." });
            await FinishRunAsync(runId, Status, [], CancellationToken.None);
            return Status;
        }
        finally { syncLock.Release(); }
    }

    private void SetState(OutreachImapSyncStatus value) { lock (stateLock) state = value; }

    private static string? Limit(string? value, int length) =>
        string.IsNullOrEmpty(value) || value.Length <= length ? value : value[..length];

    public static string CleanReply(string value)
    {
        var text = value.Replace("\r\n", "\n").Trim();
        var quoted = Regex.Match(text, @"(?im)^\s*(On .+ wrote:|From:\s*.+|_{5,}|-{5,}\s*Original Message\s*-{5,})\s*$");
        if (quoted.Success) text = text[..quoted.Index].TrimEnd();
        var signature = Regex.Match(text, @"(?m)^--\s*$");
        if (signature.Success) text = text[..signature.Index].TrimEnd();
        return string.IsNullOrWhiteSpace(text) ? "(No new readable reply text)" : text;
    }

    private sealed record InboundResult(bool Matched, bool Duplicate = false);
    private sealed record UnmatchedMessage(string ExternalId, string From, string? Subject, string Body, string? ThreadReferences);

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct)
    {
        if (!IsConfigured()) return (false, "IMAP reply sync is not fully configured.");
        try
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(configuration["Outreach:Imap:Host"] ?? "imap.zoho.in", configuration.GetValue("Outreach:Imap:Port", 993), SecureSocketOptions.SslOnConnect, ct);
            await imap.AuthenticateAsync(configuration["Outreach:Imap:Username"]!, configuration["Outreach:Imap:Password"]!, ct);
            await imap.DisconnectAsync(true, ct);
            return (true, "Zoho IMAP authentication succeeded.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Zoho IMAP connection test failed.");
            return (false, "Zoho IMAP authentication failed. Check the mailbox and app password.");
        }
    }

    private async Task<Guid> StartRunAsync(string trigger, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeetToManageDbContext>();
        var now = DateTime.UtcNow;
        await database.OutreachMailboxSyncRuns.Where(x => x.Status == "Running" && x.StartedAtUtc < now.AddMinutes(-15))
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, "Failed")
                .SetProperty(r => r.CompletedAtUtc, now)
                .SetProperty(r => r.Error, "The server stopped before this synchronization completed."), ct);
        var run = new OutreachMailboxSyncRun { Trigger = trigger, StartedAtUtc = now };
        database.OutreachMailboxSyncRuns.Add(run);
        await database.SaveChangesAsync(ct);
        return run.Id;
    }

    private async Task<Guid?> FindProspectByThreadAsync(string? inReplyTo, IEnumerable<string> references, string? subject, CancellationToken ct)
    {
        var ids = references.Append(inReplyTo).Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(x => new[] { x!, $"zoho:{x}" }).Distinct().ToList();
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeetToManageDbContext>();
        if (ids.Count > 0)
        {
            var threadMatch = await database.OutreachProspectActivities.AsNoTracking()
                .Where(x => x.ExternalId != null && ids.Contains(x.ExternalId))
                .OrderByDescending(x => x.CreatedAtUtc).Select(x => (Guid?)x.ProspectId).FirstOrDefaultAsync(ct);
            if (threadMatch is not null) return threadMatch;
        }
        var normalizedSubject = Regex.Replace(subject ?? "", @"^\s*((re|fw|fwd)\s*:\s*)+", "", RegexOptions.IgnoreCase).Trim();
        if (normalizedSubject.Length == 0) return null;
        var subjectMatches = await database.OutreachProspectActivities.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= DateTime.UtcNow.AddDays(-90)
                && (x.Type == OutreachActivityType.EmailSent || x.Type == OutreachActivityType.ReplySent)
                && x.Subject == normalizedSubject)
            .Select(x => x.ProspectId).Distinct().Take(2).ToListAsync(ct);
        return subjectMatches.Count == 1 ? subjectMatches[0] : null;
    }

    private async Task FinishRunAsync(Guid runId, OutreachImapSyncStatus result, IReadOnlyList<UnmatchedMessage> unmatched, CancellationToken ct)
    {
        if (runId == Guid.Empty) return;
        try
        {
            using var scope = scopes.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MeetToManageDbContext>();
            var run = await database.OutreachMailboxSyncRuns.FindAsync([runId], ct);
            if (run is not null)
            {
                run.CompletedAtUtc = DateTime.UtcNow;
                run.Status = result.LastError is null ? "Succeeded" : result.LastSuccessAtUtc is null ? "Failed" : "Partial";
                run.MessagesChecked = result.MessagesChecked; run.RepliesImported = result.RepliesImported;
                run.DuplicatesSkipped = result.DuplicatesSkipped; run.UnmatchedSkipped = result.UnmatchedSkipped;
                run.FailedMessages = result.FailedMessages; run.Error = Limit(result.LastError, 500);
            }
            if (unmatched.Count > 0)
            {
                var ids = unmatched.Select(x => x.ExternalId).ToList();
                var existing = await database.OutreachUnmatchedEmails.Where(x => ids.Contains(x.ExternalId)).Select(x => x.ExternalId).ToListAsync(ct);
                foreach (var message in unmatched.Where(x => !existing.Contains(x.ExternalId)))
                    database.OutreachUnmatchedEmails.Add(new OutreachUnmatchedEmail { ExternalId = message.ExternalId, From = message.From, Subject = message.Subject, Body = message.Body, ThreadReferences = message.ThreadReferences });
            }
            await database.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Could not persist outreach mailbox sync history."); }
    }

    private bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(configuration["Outreach:Imap:Username"]) &&
        !string.IsNullOrWhiteSpace(configuration["Outreach:Imap:Password"]) &&
        !string.IsNullOrWhiteSpace(configuration["Outreach:InboundWebhookSecret"]) &&
        !string.IsNullOrWhiteSpace(configuration["Api:BaseUrl"]);

    private static async Task PollAfterDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}

public sealed record OutreachImapSyncStatus(
    bool Configured,
    bool IsRunning,
    DateTime? LastAttemptAtUtc,
    DateTime? LastSuccessAtUtc,
    string? LastError,
    int MessagesChecked,
    int RepliesImported,
    int DuplicatesSkipped,
    int UnmatchedSkipped,
    int FailedMessages)
{
    public bool Healthy => Configured && LastError is null && LastSuccessAtUtc is { } success && success >= DateTime.UtcNow.AddMinutes(-10);
    public string? Alert => !Configured ? "Zoho IMAP is not configured."
        : LastError is not null ? LastError
        : LastSuccessAtUtc is null ? "The mailbox has not completed its first sync."
        : LastSuccessAtUtc < DateTime.UtcNow.AddMinutes(-10) ? "No successful mailbox sync in the last 10 minutes."
        : null;
}
