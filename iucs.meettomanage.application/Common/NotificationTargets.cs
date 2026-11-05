using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Common
{
    /// <summary>
    /// Where a notification tap should take the reader. Stored on each notification and sent in
    /// the push payload, so the web portal and the family app can each route to their own screen.
    /// </summary>
    public static class NotificationTargets
    {
        public const string Invoice = "invoice";
        public const string Class = "class";
        public const string Report = "report";
        public const string Ticket = "ticket";
        public const string Homework = "homework";
        public const string Resource = "resource";
        public const string Recording = "recording";
        public const string Home = "home";
        public const string Certificate = "certificate";

        /// <summary>
        /// Default target for notifications whose sender did not name one, inferred from the
        /// email template or notification type. Returns null when there is nowhere useful to go.
        /// </summary>
        public static string? Infer(NotificationType type, string? templateKey)
        {
            var key = templateKey ?? string.Empty;
            if (key.StartsWith("support-ticket")) return Ticket;
            if (key == "progress-report") return Report;
            if (key.StartsWith("invoice") || key.StartsWith("payment") || key.StartsWith("fee-")
                || key.StartsWith("refund") || key.Contains("payment-confirmed")) return Invoice;
            if (key.StartsWith("class-") || key.StartsWith("session-reminder") || key == "attendance-absent"
                || key == "leave-notify-parent" || key == "batch-assignment") return Class;
            if (key.StartsWith("enrollment-approved") || key == "parent-enrollment-welcome") return Home;

            return type switch
            {
                NotificationType.PaymentReminder or NotificationType.PaymentReceived or NotificationType.FeeSuspension => Invoice,
                NotificationType.SessionReminder or NotificationType.AttendanceUpdate or NotificationType.BookingConfirmation => Class,
                NotificationType.PerformanceSummary => Report,
                _ => null,
            };
        }
    }
}
