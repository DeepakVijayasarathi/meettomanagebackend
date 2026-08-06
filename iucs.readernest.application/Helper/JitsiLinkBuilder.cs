using System.Text.Json;

namespace iucs.readernest.application.Helper
{
    /// <summary>Builds the direct Jitsi room URL used in booking/reminder emails and by the live classroom.</summary>
    public static class JitsiLinkBuilder
    {
        private const string DefaultDomain = "meet.techmisai.com";

        /// <summary>
        /// <paramref name="integrationConfigJson"/> is the "jitsi" Integration's ConfigJson
        /// (expects a "domain" key); falls back to the seeded default domain if missing/unparseable.
        /// </summary>
        public static string ResolveDomain(string? integrationConfigJson)
        {
            if (!string.IsNullOrWhiteSpace(integrationConfigJson))
            {
                try
                {
                    var config = JsonSerializer.Deserialize<Dictionary<string, string>>(integrationConfigJson);
                    if (config is not null && config.TryGetValue("domain", out var configuredDomain)
                        && !string.IsNullOrWhiteSpace(configuredDomain))
                    {
                        return configuredDomain;
                    }
                }
                catch (JsonException)
                {
                    // Malformed config — fall back to the default domain.
                }
            }

            return DefaultDomain;
        }

        /// <summary>
        /// Returns null when there's no meeting room to link to. When <paramref name="token"/> is
        /// supplied (see IJitsiTokenService), it's appended as the "#jwt=" fragment Jitsi's web
        /// client reads to authenticate the join directly from an email link — once the
        /// deployment enforces token verification (see docs/JITSI_ARCHITECTURE.md), a link without
        /// a valid token for this exact room is refused instead of granting an open seat.
        /// </summary>
        public static string? BuildJoinUrl(string? meetingRoomId, string? integrationConfigJson, string? token = null)
        {
            if (string.IsNullOrWhiteSpace(meetingRoomId))
            {
                return null;
            }

            var domain = ResolveDomain(integrationConfigJson);
            var url = $"https://{domain}/{meetingRoomId}";
            return string.IsNullOrWhiteSpace(token) ? url : $"{url}#jwt={token}";
        }
    }
}
