using System;

namespace iucs.readernest.application.Helper
{
    /// <summary>
    /// Renders UTC scheduling instants (ScheduledStartAtUtc, etc.) as human-readable local
    /// time for emails/messages. The org runs on IST, so "Asia/Kolkata" is the default when
    /// no recipient-specific zone (User.TimeZoneId) is available.
    /// </summary>
    public static class DateTimeDisplay
    {
        public const string DefaultTimeZoneId = "Asia/Kolkata";

        /// <summary>Multi-timezone support: renders a UTC instant in the given (or default IST) zone.</summary>
        public static string ToLocal(DateTime utc, string? timeZoneId = null)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId ?? DefaultTimeZoneId);
                var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
                return $"{local:ddd, dd MMM yyyy h:mm tt} ({timeZoneId ?? DefaultTimeZoneId})";
            }
            catch (TimeZoneNotFoundException)
            {
                return $"{utc:u} (UTC)";
            }
        }

        /// <summary>Date-only local rendering (e.g. email subjects/summaries) — no time-of-day.</summary>
        public static string ToLocalDate(DateTime utc, string format = "dd MMM", string? timeZoneId = null)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId ?? DefaultTimeZoneId);
                return TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString(format);
            }
            catch (TimeZoneNotFoundException)
            {
                return utc.ToString(format);
            }
        }

        /// <summary>Renders a UTC start/end pair as a local time window, e.g. for leave request windows.</summary>
        public static string ToLocalRange(DateTime startUtc, DateTime endUtc, string? timeZoneId = null)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId ?? DefaultTimeZoneId);
                var start = TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone);
                var end = TimeZoneInfo.ConvertTimeFromUtc(endUtc, zone);
                var label = timeZoneId ?? DefaultTimeZoneId;
                return $"{start:dd MMM yyyy HH:mm} – {end:dd MMM yyyy HH:mm} ({label})";
            }
            catch (TimeZoneNotFoundException)
            {
                return $"{startUtc:u} – {endUtc:u}";
            }
        }
    }
}
