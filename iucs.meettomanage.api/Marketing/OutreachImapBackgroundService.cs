using System.Net.Http.Json;
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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PollAfterDelay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Zoho outreach mailbox poll failed; retrying later.");
            }
            await PollAfterDelay(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Outreach:Imap:PollSeconds", 60), 30, 900)), stoppingToken);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var username = configuration["Outreach:Imap:Username"];
        var password = configuration["Outreach:Imap:Password"];
        var secret = configuration["Outreach:InboundWebhookSecret"];
        var apiBase = configuration["Api:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(apiBase)) return;

        var host = configuration["Outreach:Imap:Host"] ?? "imap.zoho.in";
        var port = configuration.GetValue("Outreach:Imap:Port", 993);
        using var imap = new ImapClient();
        await imap.ConnectAsync(host, port, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(username, password, ct);
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        var unread = await inbox.SearchAsync(SearchQuery.NotSeen, ct);
        var http = clients.CreateClient(nameof(OutreachImapBackgroundService));
        http.DefaultRequestHeaders.Add("X-Outreach-Webhook-Secret", secret);

        foreach (var uid in unread.Take(50))
        {
            var message = await inbox.GetMessageAsync(uid, ct);
            var from = message.From.Mailboxes.FirstOrDefault()?.Address;
            if (string.IsNullOrWhiteSpace(from)) { await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); continue; }
            var payload = new
            {
                from,
                subject = message.Subject,
                text = message.TextBody ?? message.HtmlBody ?? "(No readable message body)",
                externalId = !string.IsNullOrWhiteSpace(message.MessageId) ? $"zoho:{message.MessageId}" : $"zoho-uid:{inbox.UidValidity}:{uid.Id}"
            };
            using var response = await http.PostAsJsonAsync($"{apiBase}/api/marketing/outreach/inbound/reply", payload, ct);
            if (response.IsSuccessStatusCode)
                await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
            else
                logger.LogWarning("Reply webhook rejected Zoho message {MessageId} with HTTP {Status}; leaving it unread for retry.", payload.externalId, (int)response.StatusCode);
        }
        await imap.DisconnectAsync(true, ct);
    }

    private static async Task PollAfterDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}
