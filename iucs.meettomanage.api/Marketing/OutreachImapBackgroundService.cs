using System.Net.Http.Json;
using iucs.meettomanage.application.Common;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace iucs.meettomanage.api.Marketing;

/// <summary>
/// Pulls unread messages from a dedicated Zoho mailbox and forwards them through the same
/// authenticated inbound-reply endpoint used by webhook providers. This keeps reply processing
/// idempotent and tenant-aware while requiring no paid automation service.
/// </summary>
public sealed class OutreachImapBackgroundService(
    IHttpClientFactory clients,
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
            try { await SyncNowAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Zoho outreach mailbox poll failed; retrying later.");
            }
            await PollAfterDelay(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Outreach:Imap:PollSeconds", 60), 30, 900)), stoppingToken);
        }
    }

    public async Task<OutreachImapSyncStatus> SyncNowAsync(CancellationToken ct)
    {
        if (!await syncLock.WaitAsync(0, ct)) return Status;
        SetState(Status with { IsRunning = true, LastAttemptAtUtc = DateTime.UtcNow, LastError = null });
        try
        {
        var username = configuration["Outreach:Imap:Username"];
        var password = configuration["Outreach:Imap:Password"];
        var secret = configuration["Outreach:InboundWebhookSecret"];
        var apiBase = configuration["Api:BaseUrl"]?.TrimEnd('/');
        var configured = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password) && !string.IsNullOrWhiteSpace(secret) && !string.IsNullOrWhiteSpace(apiBase);
        if (!configured)
        {
            SetState(Status with { Configured = false, IsRunning = false, LastError = "IMAP reply sync is not fully configured." });
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
            var payload = new
            {
                from = Limit(from, 256),
                subject = Limit(message.Subject, 200),
                text = Limit(body ?? "(No readable message body)", 10000),
                externalId = Limit(!string.IsNullOrWhiteSpace(message.MessageId) ? $"zoho:{message.MessageId}" : $"zoho-uid:{inbox.UidValidity}:{uid.Id}", 200)
            };
            using var response = await http.PostAsJsonAsync($"{apiBase}/api/marketing/outreach/inbound/reply", payload, ct);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<InboundResult>(cancellationToken: ct);
                await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
                if (result?.Duplicate == true) duplicateCount++;
                else if (result?.Matched == true) importedCount++;
                else unmatchedCount++;
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
        return Status;
        }
        catch (OperationCanceledException)
        {
            SetState(Status with { IsRunning = false });
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Zoho outreach mailbox sync failed.");
            SetState(Status with { IsRunning = false, LastError = "Mailbox sync failed. Check IMAP settings and the app password." });
            return Status;
        }
        finally { syncLock.Release(); }
    }

    private void SetState(OutreachImapSyncStatus value) { lock (stateLock) state = value; }

    private static string? Limit(string? value, int length) =>
        string.IsNullOrEmpty(value) || value.Length <= length ? value : value[..length];

    private sealed record InboundResult(bool Matched, bool Duplicate = false);

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
    int FailedMessages);
