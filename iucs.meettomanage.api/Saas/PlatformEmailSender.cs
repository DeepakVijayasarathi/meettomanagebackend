using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Saas;

public sealed record EmailResult(bool Delivered, string? SkippedReason = null);

/// <summary>
/// Sends the platform's own billing email (not a tenant's). Uses the SaasBilling:Smtp settings, never a
/// tenant's mail integration. Returns a "skipped" result instead of throwing when SMTP is not configured.
/// </summary>
public sealed class PlatformEmailSender(IOptions<SaasOptions> options, ILogger<PlatformEmailSender> logger)
{
    public async Task<EmailResult> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var smtp = options.Value.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            // The body can carry payment links; log only that a send was skipped.
            logger.LogInformation("Platform email not sent (SaasBilling:Smtp:Host is empty) to {To} | {Subject}", to, subject);
            return new EmailResult(false, "SMTP is not configured (SaasBilling:Smtp).");
        }
        var from = string.IsNullOrWhiteSpace(smtp.From) ? smtp.Username : smtp.From;
        if (string.IsNullOrWhiteSpace(from)) return new EmailResult(false, "SaasBilling:Smtp:From is empty.");

        using var message = new MailMessage(from, to, subject, htmlBody) { IsBodyHtml = true };
        using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.UseTls, Timeout = 15000 };
        if (!string.IsNullOrWhiteSpace(smtp.Username)) client.Credentials = new NetworkCredential(smtp.Username, smtp.Password);
        await client.SendMailAsync(message, ct);
        return new EmailResult(true);
    }
}
