using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.domain.Entities.Communication;
using iucs.meettomanage.domain.Entities.Integrations;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Services
{
    /// <summary>
    /// Sends push notifications through Firebase Cloud Messaging (HTTP v1) using the credentials the tenant stored in
    /// Settings → Integrations (the "fcm" record: projectId + serviceAccountKey, the service-account JSON). No-ops
    /// when the integration is off or unconfigured, and never fails the business operation that raised the notification.
    /// </summary>
    public class FcmPushSender : IPushSender
    {
        public const string IntegrationKey = "fcm";
        private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
        private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

        private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresAtUtc)> AccessTokens = new();

        private readonly IUnitOfWork _unitOfWork;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<FcmPushSender> _logger;

        public FcmPushSender(IUnitOfWork unitOfWork, IHttpClientFactory httpClientFactory, ILogger<FcmPushSender> logger)
        {
            _unitOfWork = unitOfWork;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task SendToUserAsync(Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var integration = await _unitOfWork.Repository<Integration>().Query().AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Key == IntegrationKey, cancellationToken);
                if (integration is not { IsEnabled: true })
                {
                    _logger.LogInformation("Push skipped for user {UserId}: the Firebase (fcm) integration is missing or not enabled in Settings → Integrations.", userId);
                    return;
                }

                var config = Normalize(JsonSerializer.Deserialize<Dictionary<string, string?>>(integration.ConfigJson ?? "{}") ?? []);
                var projectId = Value(config, "projectId");
                var account = ReadAccount(config);
                if (projectId is null || account is null)
                {
                    // Field names only (never values), so a misspelt or empty field is obvious from the log.
                    var present = string.Join(", ", config.Select(kv => string.IsNullOrWhiteSpace(kv.Value) ? kv.Key + " (empty)" : kv.Key));
                    _logger.LogWarning(
                        "Push skipped for user {UserId}: the Firebase integration needs a projectId plus either serviceAccountKey or clientEmail + privateKey. projectId: {HasProject}; service account: {HasAccount}; fields saved: [{Fields}].",
                        userId, projectId is null ? "MISSING" : "ok", account is null ? "MISSING" : "ok", present);
                    return;
                }

                var installations = await _unitOfWork.Repository<MobileAppInstallation>().Query()
                    .Where(x => x.UserId == userId && x.PushToken != null && x.NotificationsEnabled)
                    .ToListAsync(cancellationToken);
                if (installations.Count == 0)
                {
                    _logger.LogInformation("Push skipped for user {UserId}: no app installation with a push token and notifications enabled.", userId);
                    return;
                }

                var accessToken = await GetAccessTokenAsync(account.Value, cancellationToken);
                var client = _httpClientFactory.CreateClient(nameof(FcmPushSender));
                var staleTokens = false;
                // One device token can sit on several installation rows (a reinstall or cleared app data gets a new
                // installation id): send to each distinct token once, or the phone shows the same notification twice.
                foreach (var token in installations.Select(x => x.PushToken!).Distinct())
                {
                    var outcome = await SendOneAsync(client, projectId, accessToken, token, title, body, data, cancellationToken);
                    if (outcome == Outcome.Unregistered)
                    {
                        foreach (var installation in installations.Where(x => x.PushToken == token)) installation.PushToken = null;
                        staleTokens = true;
                    }
                }

                if (staleTokens) _logger.LogInformation("Cleared a stale push token for user {UserId}.", userId);
                if (staleTokens) await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Push delivery failed for user {UserId}", userId);
            }
        }

        private enum Outcome { Sent, Unregistered, Failed }

        private async Task<Outcome> SendOneAsync(HttpClient client, string projectId, string accessToken, string deviceToken,
            string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct)
        {
            var message = new Dictionary<string, object?>
            {
                ["token"] = deviceToken,
                ["notification"] = new { title, body },
                ["data"] = data ?? new Dictionary<string, string>(),
                ["android"] = new Dictionary<string, object?>
                {
                    ["priority"] = "HIGH",
                    // The notification id as the tag makes a repeat of the same notification replace the first one in the
                    // tray instead of stacking beside it.
                    ["notification"] = data is not null && data.TryGetValue("notificationId", out var notificationId)
                        ? new { channel_id = "default", tag = notificationId }
                        : (object)new { channel_id = "default" },
                },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(projectId)}/messages:send");
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            request.Content = new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return Outcome.Sent;

            var detail = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.NotFound || detail.Contains("UNREGISTERED", StringComparison.Ordinal))
                return Outcome.Unregistered;
            _logger.LogWarning("FCM rejected a push ({Status}): {Detail}", (int)response.StatusCode, detail);
            return Outcome.Failed;
        }

        /// <summary>Exchanges a signed service-account JWT for a short-lived OAuth token, cached until just before expiry.</summary>
        private async Task<string> GetAccessTokenAsync((string ClientEmail, string PrivateKey, string TokenUri) account, CancellationToken ct)
        {
            var (clientEmail, privateKey, tokenUri) = account;

            var cacheKey = clientEmail + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(privateKey)))[..16];
            if (AccessTokens.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(1)) return cached.Token;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = clientEmail, scope = Scope, aud = tokenUri, iat = now, exp = now + 3600 }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKey);
            var signature = Base64Url(rsa.SignData(Encoding.UTF8.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

            var client = _httpClientFactory.CreateClient(nameof(FcmPushSender));
            using var response = await client.PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = $"{header}.{claims}.{signature}",
            }), ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Google rejected the FCM service account ({(int)response.StatusCode}): {json}");

            using var tokenDocument = JsonDocument.Parse(json);
            var token = tokenDocument.RootElement.GetProperty("access_token").GetString()!;
            var lifetime = tokenDocument.RootElement.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3600;
            AccessTokens[cacheKey] = (token, DateTime.UtcNow.AddSeconds(lifetime));
            return token;
        }

        /// <summary>
        /// The service account from either the whole key file (serviceAccountKey) or just its two needed values
        /// (clientEmail + privateKey), which are each short enough to survive any field-length limit. The split
        /// fields win when both are present. Returns null when neither is configured; throws a clear error when the
        /// pasted JSON is incomplete.
        /// </summary>
        private static (string ClientEmail, string PrivateKey, string TokenUri)? ReadAccount(Dictionary<string, string?> config)
        {
            var email = Value(config, "clientEmail");
            var key = Value(config, "privateKey");
            if (email is not null && key is not null)
                // Pasted straight from the key file, the PEM's line breaks arrive as the two characters \ and n.
                return (email, key.Replace("\\n", "\n"), Value(config, "tokenUri") ?? DefaultTokenUri);

            var json = Value(config, "serviceAccountKey");
            if (json is null) return null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                return (root.GetProperty("client_email").GetString()!, root.GetProperty("private_key").GetString()!,
                    root.TryGetProperty("token_uri", out var uri) ? uri.GetString() ?? DefaultTokenUri : DefaultTokenUri);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"The Firebase serviceAccountKey is not a complete service-account JSON (it is {json.Length} characters; a full key file is about 2,200-2,400). " +
                    "Paste the whole file again, or use the clientEmail + privateKey fields instead.", ex);
            }
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>Field names are matched ignoring case, spaces, underscores and hyphens, so client_email, ClientEmail and clientEmail are the same field.</summary>
        private static Dictionary<string, string?> Normalize(Dictionary<string, string?> config)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in config)
                result[new string(name.Where(c => c is not ('_' or '-' or ' ')).ToArray())] = value;
            return result;
        }

        /// <summary>A trimmed value, also tolerating quotes and a trailing comma carried over from pasting a line of the key file.</summary>
        private static string? Value(Dictionary<string, string?> config, string key) =>
            config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim().Trim(',').Trim('"').Trim() : null;
    }
}
