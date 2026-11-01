using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Saas;

public sealed record EmailResult(bool Delivered, string? SkippedReason = null);

/// <summary>
/// Sends the platform's own billing email (not a tenant's). Uses the SaasBilling:Smtp settings, never a
/// tenant's mail integration; without SMTP it falls back to the platform's Resend account (Resend:ApiKey),
/// the same fallback the app's other mail uses. Returns a "skipped" result when neither is configured.
/// </summary>
public sealed class PlatformEmailSender(IOptions<SaasOptions> options, ILogger<PlatformEmailSender> logger,
    IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    public async Task<EmailResult> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var smtp = options.Value.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            if (configuration["Resend:ApiKey"] is { Length: > 0 } apiKey)
                return await SendViaResendAsync(apiKey, to, subject, htmlBody, ct);
            // The body can carry payment links; log only that a send was skipped.
            logger.LogInformation("Platform email not sent (no SaasBilling:Smtp and no Resend key) to {To} | {Subject}", to, subject);
            return new EmailResult(false, "Neither SMTP (SaasBilling:Smtp) nor Resend (Resend:ApiKey) is configured.");
        }
        var from = string.IsNullOrWhiteSpace(smtp.From) ? smtp.Username : smtp.From;
        if (string.IsNullOrWhiteSpace(from)) return new EmailResult(false, "SaasBilling:Smtp:From is empty.");

        using var message = new MailMessage(from, to, subject, htmlBody) { IsBodyHtml = true };
        using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.UseTls, Timeout = 15000 };
        if (!string.IsNullOrWhiteSpace(smtp.Username)) client.Credentials = new NetworkCredential(smtp.Username, smtp.Password);
        await client.SendMailAsync(message, ct);
        return new EmailResult(true);
    }

    private async Task<EmailResult> SendViaResendAsync(string apiKey, string to, string subject, string htmlBody, CancellationToken ct)
    {
        var from = configuration["Resend:From"] is { Length: > 0 } f ? f : $"{options.Value.PlatformName} <notifications@meettomanage.cloud>";
        var client = httpClientFactory.CreateClient(nameof(PlatformEmailSender));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(new { from, to = new[] { to }, subject, html = htmlBody }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            // Thrown like an SMTP failure so the reminder log records it and retries.
            throw new InvalidOperationException($"Resend refused the email ({(int)response.StatusCode}): {detail[..Math.Min(detail.Length, 300)]}");
        }
        return new EmailResult(true);
    }
}
