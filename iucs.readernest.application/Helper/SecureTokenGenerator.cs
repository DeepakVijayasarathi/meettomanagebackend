using System.Security.Cryptography;

namespace iucs.readernest.application.Helper
{
    /// <summary>
    /// Generates unguessable bearer tokens for one-off, single-use links (currently just the
    /// self-service PIN reset email). 256 bits of entropy, URL-safe Base64 — safe to embed
    /// directly in a query string with no additional encoding.
    /// </summary>
    public static class SecureTokenGenerator
    {
        public static string Generate() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
    }
}
