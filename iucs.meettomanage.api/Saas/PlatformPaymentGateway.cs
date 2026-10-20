using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Entities.Saas;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Saas;

public sealed record PaymentLink(string Reference, string Url);

/// <summary>The platform's own collection gateway (separate from the tenants' fee-collection gateways).</summary>
public interface IPlatformPaymentGateway
{
    string Name { get; }
    /// <summary>Null when this gateway cannot create a link (manual collection).</summary>
    Task<PaymentLink?> CreatePaymentLinkAsync(PlatformInvoice invoice, string tenantName, string? payerEmail, CancellationToken ct);
    bool VerifyWebhookSignature(string body, string? signature);
}

/// <summary>No online collection: the operator confirms payments (bank transfer, UPI, cash) from the console.</summary>
public sealed class ManualPlatformGateway : IPlatformPaymentGateway
{
    public string Name => "manual";
    public Task<PaymentLink?> CreatePaymentLinkAsync(PlatformInvoice invoice, string tenantName, string? payerEmail, CancellationToken ct) =>
        Task.FromResult<PaymentLink?>(null);
    public bool VerifyWebhookSignature(string body, string? signature) => false;
}

/// <summary>Razorpay Payment Links; the payment_link.paid webhook settles the invoice.</summary>
public sealed class RazorpayPlatformGateway(IHttpClientFactory httpClients, IOptions<SaasOptions> options,
    ILogger<RazorpayPlatformGateway> logger) : IPlatformPaymentGateway
{
    public string Name => "razorpay";

    public async Task<PaymentLink?> CreatePaymentLinkAsync(PlatformInvoice invoice, string tenantName, string? payerEmail, CancellationToken ct)
    {
        var razorpay = options.Value.Razorpay;
        if (string.IsNullOrWhiteSpace(razorpay.KeyId) || string.IsNullOrWhiteSpace(razorpay.KeySecret))
            throw new ExternalServiceException("Razorpay is selected for platform billing but SaasBilling:Razorpay keys are not configured.");

        var payload = JsonSerializer.Serialize(new
        {
            amount = (long)Math.Round(invoice.Total * 100m), // paise
            currency = invoice.Currency,
            reference_id = invoice.InvoiceNumber,
            description = $"{options.Value.PlatformName} subscription {invoice.InvoiceNumber} - {tenantName}",
            customer = string.IsNullOrWhiteSpace(payerEmail) ? null : new { email = payerEmail },
            notes = new { platformInvoiceId = invoice.Id.ToString(), invoiceNumber = invoice.InvoiceNumber },
            expire_by = new DateTimeOffset(DateTime.SpecifyKind(invoice.DueAtUtc, DateTimeKind.Utc).AddDays(60)).ToUnixTimeSeconds(),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.razorpay.com/v1/payment_links");
        request.Headers.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{razorpay.KeyId}:{razorpay.KeySecret}")));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await httpClients.CreateClient(nameof(RazorpayPlatformGateway)).SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Razorpay payment-link creation failed for {Invoice}: {Status} {Body}", invoice.InvoiceNumber, (int)response.StatusCode, body);
            throw new ExternalServiceException("The payment gateway could not create a payment link. Try again shortly.");
        }
        using var json = JsonDocument.Parse(body);
        var id = json.RootElement.GetProperty("id").GetString()!;
        var url = json.RootElement.GetProperty("short_url").GetString()!;
        return new PaymentLink(id, url);
    }

    public bool VerifyWebhookSignature(string body, string? signature)
    {
        var secret = options.Value.Razorpay.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature)) return false;
        var expected = Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }
}
