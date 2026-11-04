using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace iucs.meettomanage.api.Monitoring;

/// <summary>
/// Strips anything that could identify a family from a family-app error before it is stored, and computes the
/// fingerprint that groups repeats of the same error.
/// </summary>
public static partial class CrashReportSanitizer
{
    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")] private static partial Regex Email();
    [GeneratedRegex(@"(https?://[^\s?#""']+)[?#][^\s""']*")] private static partial Regex UrlQuery();
    [GeneratedRegex(@"eyJ[\w-]+\.[\w-]+\.[\w-]*")] private static partial Regex Jwt();
    [GeneratedRegex(@"\b[A-Za-z0-9_\-]{32,}\b")] private static partial Regex LongToken();
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")] private static partial Regex Guid();
    [GeneratedRegex(@"\+?\d[\d\s-]{4,}\d")] private static partial Regex LongNumber();
    [GeneratedRegex(@":\d+(:\d+)?\)?$")] private static partial Regex LineColumn();

    public static string Scrub(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        text = Jwt().Replace(text, "[token]");
        text = Email().Replace(text, "[email]");
        text = UrlQuery().Replace(text, "$1");
        text = Guid().Replace(text, "[id]");
        text = LongToken().Replace(text, "[token]");
        text = LongNumber().Replace(text, "[n]");
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>The first few stack frames, scrubbed. Bundled JS has no source paths worth keeping beyond names.</summary>
    public static string? StackTop(string? stack, int frames = 6)
    {
        if (string.IsNullOrWhiteSpace(stack)) return null;
        var lines = stack.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(frames);
        return Scrub(string.Join('\n', lines), 1500);
    }

    /// <summary>
    /// Same error name + same top frames = same crash. Line/column numbers are dropped so a rebuild of the same code
    /// still groups together; the message is left out because it often carries values.
    /// </summary>
    public static string Fingerprint(string name, string? stackTop, string? context)
    {
        var frames = (stackTop ?? string.Empty).Split('\n').Take(3).Select(f => LineColumn().Replace(f.Trim(), string.Empty));
        var key = $"{name}|{context}|{string.Join('|', frames)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
    }
}
