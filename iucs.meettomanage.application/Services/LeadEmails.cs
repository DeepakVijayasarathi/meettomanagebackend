using System.Globalization;
using System.Net;
using iucs.meettomanage.domain.Entities.Marketing;

namespace iucs.meettomanage.application.Services
{
    /// <summary>
    /// The e-mails the lead lifecycle sends: to the lead (acknowledgement, demo confirmation and reminders, recap)
    /// and to the team (high-intent alert). Plain, short and personal; every link is built from the site URL.
    /// </summary>
    public static class LeadEmails
    {
        public sealed record Email(string Subject, string Html);

        private const string WhatsAppUrl = "https://wa.me/919344773231";

        private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");

        private static string FirstName(PlatformDemoRequest lead)
        {
            var first = lead.FullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(first) ? "there" : first;
        }

        private static string Wrap(string inner) =>
            $"<div style=\"font-family:Arial,sans-serif;font-size:15px;line-height:1.6;color:#1c1917;max-width:560px\">{inner}" +
            "<p style=\"margin-top:24px\">The Meet to Manage team</p></div>";

        private static string Button(string url, string label) =>
            $"<p><a href=\"{H(url)}\" style=\"display:inline-block;background:#c2410c;color:#fff;padding:10px 18px;border-radius:8px;text-decoration:none;font-weight:bold\">{H(label)}</a></p>";

        /// <summary>Demo time in the team's time zone (India by default), e.g. "Tue 18 Nov, 4:30 PM IST".</summary>
        public static string FormatDemoTime(DateTime utc, string timeZoneId = "Asia/Kolkata")
        {
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; }
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            var label = zone.Id is "Asia/Kolkata" or "India Standard Time" ? "IST" : zone == TimeZoneInfo.Utc ? "UTC" : zone.StandardName;
            return $"{local.ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture)} {label}";
        }

        public static Email Acknowledgement(PlatformDemoRequest lead, string siteUrl, string? ownerName, string? bookingUrl)
        {
            var who = string.IsNullOrWhiteSpace(ownerName) ? "Someone from our team" : H(ownerName);
            var book = string.IsNullOrWhiteSpace(bookingUrl)
                ? $"<p>Prefer to sort it out now? <a href=\"{WhatsAppUrl}\">Message us on WhatsApp</a> and we'll fix a time.</p>"
                : $"<p>Want to pick a time yourself?</p>{Button(bookingUrl!, "Choose a demo time")}";
            return new("We got your Meet to Manage demo request",
                Wrap($"<p>Hi {H(FirstName(lead))},</p><p>Thanks for asking about Meet to Manage for {H(lead.AcademyName)}. {who} will contact you within one business day to set up your demo.</p>"
                    + book
                    + $"<p>While you wait, you can click around a sample academy, no sign-up needed: <a href=\"{H(siteUrl)}/demo-login\">try the live demo</a>.</p>"));
        }

        public static Email DemoConfirmation(PlatformDemoRequest lead, string when) =>
            new($"Your Meet to Manage demo: {when}",
                Wrap($"<p>Hi {H(FirstName(lead))},</p><p>Your demo is booked for <b>{H(when)}</b>. We'll walk through live classes, admissions, fees and the parent app using an academy like yours.</p>"
                    + "<p>It helps to bring: roughly how many students you have, how you collect fees today and the tools you use now.</p>"
                    + $"<p>Need to change the time? <a href=\"{WhatsAppUrl}\">Message us on WhatsApp</a> or reply to this e-mail.</p>"));

        public static Email DemoReminderDay(PlatformDemoRequest lead, string when) =>
            new("Reminder: your Meet to Manage demo is tomorrow",
                Wrap($"<p>Hi {H(FirstName(lead))},</p><p>Just a reminder that your demo is on <b>{H(when)}</b>. If that time no longer works, <a href=\"{WhatsAppUrl}\">tell us on WhatsApp</a> and we'll move it.</p>"));

        public static Email DemoReminderHour(PlatformDemoRequest lead, string when) =>
            new("Your Meet to Manage demo starts in an hour",
                Wrap($"<p>Hi {H(FirstName(lead))},</p><p>See you soon: your demo starts at <b>{H(when)}</b>. We'll join using the contact method we agreed. Running late? <a href=\"{WhatsAppUrl}\">Let us know on WhatsApp</a>.</p>"));

        public static Email DemoRecap(PlatformDemoRequest lead, string siteUrl, string? note)
        {
            var notePart = string.IsNullOrWhiteSpace(note) ? "" : $"<p>From our conversation: {H(note)}</p>";
            return new("Thanks for the demo: your next step",
                Wrap($"<p>Hi {H(FirstName(lead))},</p><p>Thanks for your time today. Here's the quickest way to see Meet to Manage running {H(lead.AcademyName)}:</p>"
                    + notePart
                    + Button($"{siteUrl}/signup?plan=trial", "Start your free trial")
                    + "<p>Every module is unlocked during the trial and no card is needed. Add a batch and a few students, then run one live class. That's usually enough to know whether it fits.</p>"
                    + $"<p>Questions about pricing or moving your data? <a href=\"{H(siteUrl)}/pricing\">See the plans</a> or reply to this e-mail.</p>"));
        }

        public static Email HighIntentAlert(PlatformDemoRequest lead, string consoleUrl, int pricingViews) =>
            new($"Ready to buy? {lead.FullName} ({lead.AcademyName}) keeps checking pricing",
                $"<p><b>{H(lead.FullName)}</b> from <b>{H(lead.AcademyName)}</b> ({H(LeadLifecycle.Label(lead.Stage))}) viewed the pricing page {pricingViews} times in the last {LeadLifecycle.HighIntentWindowDays} days.</p>"
                + $"<p>This is a good moment to offer a plan comparison or a short call.</p><p>Phone: {H(lead.Phone)} · Email: {H(lead.WorkEmail)}</p>"
                + $"<p><a href=\"{H(consoleUrl)}\">Open the lead in the Marketing console</a></p>");
    }
}
