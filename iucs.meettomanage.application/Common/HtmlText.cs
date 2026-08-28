using System.Text.RegularExpressions;

namespace iucs.meettomanage.application.Common
{
    /// <summary>
    /// Strips markup down to a readable plain-text line. Used both when a notification is
    /// first written (<see cref="Services.NotificationService"/>) and to backfill rows
    /// written before that stripping existed (<see cref="Services.NotificationService"/>
    /// callers predate it, so old <c>Notification.Body</c> values still hold raw HTML).
    /// </summary>
    public static class HtmlText
    {
        private const string Header = "The Reader Nest";
        private const string Footer = "The Reader Nest · Read · Write · Speak";

        public static string PlainTextFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return html;
            }

            var text = Regex.Replace(html, "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (text.StartsWith(Header, StringComparison.Ordinal))
            {
                text = text[Header.Length..].TrimStart();
            }
            if (text.EndsWith(Footer, StringComparison.Ordinal))
            {
                text = text[..^Footer.Length].TrimEnd();
            }

            return text;
        }
    }
}
