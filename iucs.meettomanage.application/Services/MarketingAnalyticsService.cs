using System.Text.RegularExpressions;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.application.Services
{
    public partial class MarketingAnalyticsService : IMarketingAnalyticsService
    {
        /// <summary>Anonymous rows, but still no reason to keep them forever.</summary>
        private static readonly TimeSpan Retention = TimeSpan.FromDays(400);

        private const string ChatBookingMarker = "Booked by the website chat assistant.";

        private readonly IUnitOfWork _unitOfWork;

        public MarketingAnalyticsService(IUnitOfWork unitOfWork)
        {
            _unitOfWork = unitOfWork;
        }

        public async Task TrackAsync(TrackMarketingEventRequest request, string? userAgent, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userAgent) || BotPattern().IsMatch(userAgent))
            {
                return;
            }

            var path = request.Path.Split('?', '#')[0].Trim();
            if (!path.StartsWith('/') || path.Length > 200 || !SafePath().IsMatch(path))
            {
                return;
            }

            await _unitOfWork.Repository<MarketingEvent>().AddAsync(new MarketingEvent
            {
                Type = request.Type,
                Path = path.Length > 1 ? path.TrimEnd('/') : path,
                VisitorId = request.VisitorId,
                Source = Clean(request.UtmSource) ?? CleanHost(request.ReferrerHost),
                Medium = Clean(request.UtmMedium),
                Campaign = Clean(request.UtmCampaign),
                Device = DeviceOf(userAgent),
            }, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task<MarketingDashboardDto> GetDashboardAsync(int days, int tzOffsetMinutes, CancellationToken cancellationToken = default)
        {
            days = Math.Clamp(days, 1, 90);
            var offset = TimeSpan.FromMinutes(Math.Clamp(tzOffsetMinutes, -14 * 60, 14 * 60));
            var nowUtc = DateTime.UtcNow;

            // Window = the last `days` local calendar days including today; the previous window is
            // the same length immediately before it.
            var todayLocal = (nowUtc + offset).Date;
            var startLocal = todayLocal.AddDays(-(days - 1));
            var startUtc = DateTime.SpecifyKind(startLocal - offset, DateTimeKind.Utc);
            var previousStartUtc = startUtc.AddDays(-days);

            await _unitOfWork.Repository<MarketingEvent>()
                .ExecuteHardDeleteAsync(e => e.CreatedAtUtc < nowUtc - Retention, cancellationToken);

            var events = await _unitOfWork.Repository<MarketingEvent>().Query()
                .Where(e => e.CreatedAtUtc >= previousStartUtc)
                .Select(e => new { e.CreatedAtUtc, e.Type, e.Path, e.VisitorId, e.Source, e.Campaign, e.Device })
                .ToListAsync(cancellationToken);
            var requests = await _unitOfWork.Repository<PlatformDemoRequest>().Query()
                .Where(r => r.CreatedAtUtc >= previousStartUtc)
                .Select(r => new { r.CreatedAtUtc, r.Message, r.Status })
                .ToListAsync(cancellationToken);

            var current = events.Where(e => e.CreatedAtUtc >= startUtc).ToList();
            var previous = events.Where(e => e.CreatedAtUtc < startUtc).ToList();
            var currentRequests = requests.Where(r => r.CreatedAtUtc >= startUtc).ToList();
            var previousRequests = requests.Where(r => r.CreatedAtUtc < startUtc).ToList();

            MarketingTotalsDto Totals(
                IEnumerable<(string Type, string VisitorId)> evts,
                IEnumerable<string?> requestMessages)
            {
                var list = evts.ToList();
                var messages = requestMessages.ToList();
                var visitors = list.Where(e => e.Type == MarketingEvent.PageView).Select(e => e.VisitorId).Distinct().Count();
                return new MarketingTotalsDto
                {
                    PageViews = list.Count(e => e.Type == MarketingEvent.PageView),
                    Visitors = visitors,
                    ChatsStarted = list.Count(e => e.Type == MarketingEvent.ChatStarted),
                    DemoRequests = messages.Count,
                    ChatBookings = messages.Count(m => m != null && m.StartsWith(ChatBookingMarker)),
                    ConversionRate = visitors == 0 ? 0 : Math.Round(messages.Count * 100.0 / visitors, 1),
                };
            }

            var views = current.Where(e => e.Type == MarketingEvent.PageView).ToList();
            string LocalDay(DateTime utc) => (utc + offset).ToString("yyyy-MM-dd");

            var daily = Enumerable.Range(0, days)
                .Select(i => startLocal.AddDays(i).ToString("yyyy-MM-dd"))
                .Select(day => new MarketingDailyPointDto
                {
                    Date = day,
                    PageViews = views.Count(e => LocalDay(e.CreatedAtUtc) == day),
                    Visitors = views.Where(e => LocalDay(e.CreatedAtUtc) == day).Select(e => e.VisitorId).Distinct().Count(),
                    DemoRequests = currentRequests.Count(r => LocalDay(r.CreatedAtUtc) == day),
                })
                .ToList();

            static List<NamedCountDto> Top(IEnumerable<string> names, int take) =>
                names.GroupBy(n => n)
                    .Select(g => new NamedCountDto { Name = g.Key, Count = g.Count() })
                    .OrderByDescending(n => n.Count).ThenBy(n => n.Name)
                    .Take(take)
                    .ToList();

            // Sources and devices count visitors (first-touch per visitor in the window), not raw
            // page views, so one visitor clicking around doesn't inflate a channel.
            var firstTouch = views
                .OrderBy(e => e.CreatedAtUtc)
                .GroupBy(e => e.VisitorId)
                .Select(g => g.First())
                .ToList();

            return new MarketingDashboardDto
            {
                Days = days,
                Totals = Totals(current.Select(e => (e.Type, e.VisitorId)), currentRequests.Select(r => r.Message)),
                Previous = Totals(previous.Select(e => (e.Type, e.VisitorId)), previousRequests.Select(r => r.Message)),
                Daily = daily,
                TopPages = Top(views.Select(e => e.Path), 10),
                TopBlogPosts = Top(views.Where(e => e.Path.StartsWith("/blog/")).Select(e => e.Path), 10),
                Sources = Top(firstTouch.Select(e => SourceLabel(e.Source)), 10),
                Campaigns = Top(firstTouch.Where(e => e.Campaign != null).Select(e => e.Campaign!), 10),
                Devices = Top(firstTouch.Select(e => e.Device), 3),
                DemoRequestsByStatus = Top(currentRequests.Select(r => r.Status.ToString()), 10),
            };
        }

        /// <summary>Folds the many hostnames one network uses into one readable channel name.</summary>
        public static string SourceLabel(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return "Direct";
            }

            var s = source.ToLowerInvariant();
            if (s.Contains("google")) return "Google";
            if (s.Contains("facebook") || s == "fb") return "Facebook";
            if (s.Contains("instagram") || s == "ig") return "Instagram";
            if (s.Contains("linkedin") || s == "lnkd.in") return "LinkedIn";
            if (s.Contains("youtube") || s == "youtu.be") return "YouTube";
            if (s == "t.co" || s.Contains("twitter") || s == "x.com") return "X (Twitter)";
            if (s.Contains("whatsapp") || s == "wa.me") return "WhatsApp";
            if (s.Contains("bing")) return "Bing";
            if (s.Contains("chatgpt") || s.Contains("openai")) return "ChatGPT";
            if (s.Contains("producthunt")) return "Product Hunt";
            return s;
        }

        private static string? Clean(string? value)
        {
            var trimmed = value?.Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(trimmed) || trimmed.Length > 100 || !SafeToken().IsMatch(trimmed) ? null : trimmed;
        }

        private static string? CleanHost(string? host)
        {
            var cleaned = Clean(host);
            return cleaned is null ? null : cleaned.StartsWith("www.") ? cleaned[4..] : cleaned;
        }

        private static string DeviceOf(string userAgent)
        {
            if (TabletPattern().IsMatch(userAgent)) return "tablet";
            if (MobilePattern().IsMatch(userAgent)) return "mobile";
            return "desktop";
        }

        [GeneratedRegex("bot|crawl|spider|slurp|facebookexternalhit|embedly|preview|headless|lighthouse|pingdom|uptime|monitor|curl|wget|python-requests", RegexOptions.IgnoreCase)]
        private static partial Regex BotPattern();

        [GeneratedRegex(@"ipad|tablet|android(?!.*mobile)", RegexOptions.IgnoreCase)]
        private static partial Regex TabletPattern();

        [GeneratedRegex(@"mobi|iphone|ipod|android", RegexOptions.IgnoreCase)]
        private static partial Regex MobilePattern();

        [GeneratedRegex(@"^/[A-Za-z0-9/_\-.]*$")]
        private static partial Regex SafePath();

        [GeneratedRegex(@"^[a-z0-9._\-+ ]+$")]
        private static partial Regex SafeToken();
    }
}
