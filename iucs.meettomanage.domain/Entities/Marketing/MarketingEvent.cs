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
    }
}
