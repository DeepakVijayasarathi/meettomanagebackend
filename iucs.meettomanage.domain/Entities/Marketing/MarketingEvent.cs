using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Marketing
{
    /// <summary>
    /// One anonymous event on the public marketing site (a page view, or a visitor starting the
    /// chat assistant), behind the marketing console's own analytics dashboard. Deliberately
    /// minimal: no IP address, no user agent, no account link — just a random per-browser visitor
    /// id, where the visit came from, and the device class. Bots are dropped before they get here.
    /// CreatedAtUtc (stamped on save) is when it happened.
    /// </summary>
    [Index(nameof(CreatedAtUtc))]
    public class MarketingEvent : BaseEntity
    {
        public const string PageView = "page_view";
        public const string ChatStarted = "chat_started";
        public const string FormStarted = "form_started";
        public const string FormValidationFailed = "form_validation_failed";
        public const string FormAbandoned = "form_abandoned";
        public const string FormSubmitted = "form_submitted";
        public const string WhatsAppClicked = "whatsapp_clicked";
        public const string ExitIntentShown = "exit_intent_shown";
        public const string ExitIntentSubmitted = "exit_intent_submitted";
        /// <summary>The visitor saw one variant of an A/B test; <see cref="Variant"/> says which.</summary>
        public const string ExperimentViewed = "experiment_viewed";
        /// <summary>The visitor clicked "Sign in" in the marketing site's menu.</summary>
        public const string SignInClicked = "signin_clicked";
        /// <summary>The visitor clicked a demo account on the login page; <see cref="Variant"/> is "demo-login:&lt;role&gt;".</summary>
        public const string DemoLoginClicked = "demo_login_clicked";
        /// <summary>The visitor followed a contextual content link to a product workflow.</summary>
        public const string ContentCtaClicked = "content_cta_clicked";
        /// <summary>The visitor clicked a primary Book demo or Schedule call action.</summary>
        public const string SalesCtaClicked = "sales_cta_clicked";

        [MaxLength(32)]
        public string Type { get; set; } = PageView;

        [MaxLength(200)]
        public string Path { get; set; } = "/";

        /// <summary>Random id the browser keeps in localStorage — counts unique visitors, identifies no one.</summary>
        [MaxLength(64)]
        public string VisitorId { get; set; } = null!;

        /// <summary>utm_source if present, else the external referrer's host, else null (direct).</summary>
        [MaxLength(100)]
        public string? Source { get; set; }

        [MaxLength(100)]
        public string? Medium { get; set; }

        [MaxLength(100)]
        public string? Campaign { get; set; }

        /// <summary>"mobile", "tablet" or "desktop", from the request's user agent.</summary>
        [MaxLength(16)]
        public string Device { get; set; } = "desktop";

        /// <summary>ISO 3166-1 alpha-2 region reported by the browser locale; no IP address is stored.</summary>
        [MaxLength(2)]
        public string? Country { get; set; }

        /// <summary>Extra label as "group:value": an A/B test bucket (e.g. "getstarted-copy:b") or the
        /// demo account clicked (e.g. "demo-login:teacher"); null when there's nothing to add.</summary>
        [MaxLength(48)]
        public string? Variant { get; set; }
    }
}
