using System.Security.Cryptography;
using System.Text;

namespace iucs.meettomanage.application.Helper
{
    /// <summary>
    /// Time-based one-time passwords (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits) — the codes shown by
    /// Google Authenticator, Microsoft Authenticator, 1Password and similar apps.
    /// </summary>
    public static class Totp
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        private const int StepSeconds = 30;

        public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

        public static string ToBase32(byte[] data)
        {
            var output = new StringBuilder((data.Length + 4) / 5 * 8);
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0) output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
            return output.ToString();
        }

        public static byte[] FromBase32(string text)
        {
            var clean = text.Trim().TrimEnd('=').Replace(" ", string.Empty).ToUpperInvariant();
            var output = new List<byte>(clean.Length * 5 / 8);
            int buffer = 0, bits = 0;
            foreach (var c in clean)
            {
                var value = Base32Alphabet.IndexOf(c);
                if (value < 0) throw new FormatException("Invalid base32 character.");
                buffer = (buffer << 5) | value;
                bits += 5;
                if (bits >= 8)
                {
                    output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                    bits -= 8;
                }
            }
            return output.ToArray();
        }

        public static string Code(byte[] secret, DateTime utcNow) => Code(secret, Counter(utcNow));

        public static string Code(byte[] secret, long counter)
        {
            Span<byte> message = stackalloc byte[8];
            for (var i = 7; i >= 0; i--)
            {
                message[i] = (byte)(counter & 0xFF);
                counter >>= 8;
            }
            var hash = HMACSHA1.HashData(secret, message);
            var offset = hash[^1] & 0x0F;
            var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            return (binary % 1_000_000).ToString("D6");
        }

        /// <summary>
        /// Checks a code against the current step and one step either side (clock drift). Returns the matched
        /// step so callers can refuse to accept the same code twice.
        /// </summary>
        public static long? Verify(byte[] secret, string? code, DateTime utcNow, long? lastUsedStep = null)
        {
            var digits = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length != 6) return null;
            var now = Counter(utcNow);
            for (var step = now - 1; step <= now + 1; step++)
            {
                if (lastUsedStep is not null && step <= lastUsedStep) continue;
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(digits)))
                    return step;
            }
            return null;
        }

        public static string OtpAuthUri(string issuer, string account, string base32Secret) =>
            $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period={StepSeconds}";

        private static long Counter(DateTime utcNow) => new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds() / StepSeconds;
    }
}
