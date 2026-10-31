using System.Text.RegularExpressions;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;
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
                Country = CleanCountry(request.Country),
            }, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// The reporting window: the last <paramref name="days"/> local calendar days including today,
        /// or the custom from–to range (a future end is capped at today, a reversed range swapped,
        /// at most 183 days so a comparison window stays inside the 400-day retention).
        /// </summary>
        private static (int Days, TimeSpan Offset, DateTime StartLocal, DateTime EndLocal, DateTime StartUtc, DateTime EndUtc) Window(
            int days, int tzOffsetMinutes, DateOnly? from, DateOnly? to)
        {
            days = Math.Clamp(days, 1, 90);
            var offset = TimeSpan.FromMinutes(Math.Clamp(tzOffsetMinutes, -14 * 60, 14 * 60));
            var todayLocal = (DateTime.UtcNow + offset).Date;
            var endLocal = todayLocal;
            if (from is { } fromDate && to is { } toDate)
            {
                var a = fromDate.ToDateTime(TimeOnly.MinValue);
                var b = toDate.ToDateTime(TimeOnly.MinValue);
                if (b < a) (a, b) = (b, a);
                endLocal = b > todayLocal ? todayLocal : b;
                if (a > endLocal) a = endLocal;
                days = Math.Min((int)(endLocal - a).TotalDays + 1, 183);
            }
            var startLocal = endLocal.AddDays(-(days - 1));
            return (days, offset, startLocal, endLocal,
                DateTime.SpecifyKind(startLocal - offset, DateTimeKind.Utc),
                DateTime.SpecifyKind(endLocal.AddDays(1) - offset, DateTimeKind.Utc));
        }

        public async Task<MarketingVisitorJourneysDto> GetVisitorJourneysAsync(
            int days, int tzOffsetMinutes, int page, int pageSize, string? country = null,
            CancellationToken cancellationToken = default, DateOnly? from = null, DateOnly? to = null)
        {
            (days, var offset, var startLocal, var endLocal, var startUtc, var endUtc) = Window(days, tzOffsetMinutes, from, to);
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 100);

            var events = await _unitOfWork.Repository<MarketingEvent>().Query()
                .Where(e => e.CreatedAtUtc >= startUtc && e.CreatedAtUtc < endUtc)
                .OrderBy(e => e.CreatedAtUtc)
                .Select(e => new { e.CreatedAtUtc, e.Type, e.Path, e.VisitorId, e.Source, e.Campaign, e.Device, e.Country })
                .ToListAsync(cancellationToken);
            // A visitor counts as having booked when a demo request carries their visitor id.
            var bookedVisitors = (await _unitOfWork.Repository<PlatformDemoRequest>().Query()
                    .Where(r => r.VisitorId != null && r.CreatedAtUtc >= startUtc && r.CreatedAtUtc < endUtc)
                    .Select(r => r.VisitorId!)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            var visitors = events
                .GroupBy(e => e.VisitorId)
                .Where(g => g.Any(e => e.Type == MarketingEvent.PageView))
                .Select(g =>
                {
                    var views = g.Where(e => e.Type == MarketingEvent.PageView).ToList();
                    var first = views[0];
                    var visitorCountry = g.Select(e => e.Country).LastOrDefault(c => c != null);
                    return new MarketingVisitorJourneyDto
                    {
                        Visitor = g.Key.Length > 8 ? g.Key[^8..] : g.Key,
                        VisitorId = g.Key,
                        FirstSeenUtc = first.CreatedAtUtc,
                        LastSeenUtc = g.Last().CreatedAtUtc,
                        Country = visitorCountry,
                        Device = first.Device,
                        Source = first.Source ?? "Direct",
                        Campaign = first.Campaign,
                        PageViews = views.Count,
                        Pages = views.Select(e => new MarketingJourneyStepDto { Path = e.Path, AtUtc = e.CreatedAtUtc }).ToList(),
                        StartedChat = g.Any(e => e.Type == MarketingEvent.ChatStarted),
                        BookedDemo = bookedVisitors.Contains(g.Key),
                        ReturnVisits = views.Select(e => (e.CreatedAtUtc + offset).Date).Distinct().Count() - 1,
                    };
                })
                .ToList();

            var countryFilter = string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant();
            var filtered = countryFilter is null
                ? visitors
                : visitors.Where(v => (v.Country ?? "?") == countryFilter).ToList();

            static List<NamedCountDto> Count(IEnumerable<string> names, int take) =>
                names.GroupBy(n => n).Select(g => new NamedCountDto { Name = g.Key, Count = g.Count() })
                    .OrderByDescending(n => n.Count).ThenBy(n => n.Name).Take(take).ToList();

            var hours = new int[24];
            foreach (var v in filtered) hours[(v.FirstSeenUtc + offset).Hour]++;

            return new MarketingVisitorJourneysDto
            {
                Days = days,
                StartDate = startLocal.ToString("yyyy-MM-dd"),
                EndDate = endLocal.ToString("yyyy-MM-dd"),
                TotalVisitors = filtered.Count,
                SinglePageVisitors = filtered.Count(v => v.PageViews == 1),
                ReturningVisitors = filtered.Count(v => v.ReturnVisits > 0),
                ChatVisitors = filtered.Count(v => v.StartedChat),
                BookedVisitors = filtered.Count(v => v.BookedDemo),
                Countries = Count(visitors.Select(v => v.Country ?? "?"), 20),
                EntryPages = Count(filtered.Select(v => v.Pages[0].Path), 10),
                ExitPages = Count(filtered.Select(v => v.Pages[^1].Path), 10),
                TopJourneys = Count(filtered.Where(v => v.PageViews > 1).Select(v => string.Join(" → ", Collapse(v.Pages.Select(p => p.Path)).Take(4))), 10),
                VisitsByHour = hours.Select((n, h) => new NamedCountDto { Name = h.ToString("00"), Count = n }).ToList(),
                Visitors = filtered.OrderByDescending(v => v.LastSeenUtc).Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                Page = page,
                PageSize = pageSize,
            };
        }

        private static readonly TimeSpan VisitGap = TimeSpan.FromMinutes(30);

        public async Task<MarketingVisitorDetailDto?> GetVisitorDetailAsync(string visitorId, CancellationToken cancellationToken = default)
        {
            var events = await _unitOfWork.Repository<MarketingEvent>().Query()
                .Where(e => e.VisitorId == visitorId)
                .OrderBy(e => e.CreatedAtUtc)
                .Select(e => new { e.CreatedAtUtc, e.Type, e.Path, e.Source, e.Campaign, e.Device, e.Country })
                .ToListAsync(cancellationToken);
            if (events.Count == 0) return null;

            var visits = new List<MarketingVisitDto>();
            foreach (var e in events)
            {
                var current = visits.LastOrDefault();
                if (current is null || e.CreatedAtUtc - current.EndUtc > VisitGap)
                {
                    current = new MarketingVisitDto { StartUtc = e.CreatedAtUtc, EndUtc = e.CreatedAtUtc, Source = e.Source ?? "Direct", Campaign = e.Campaign, Device = e.Device };
                    visits.Add(current);
                }
                current.EndUtc = e.CreatedAtUtc;
                if (e.Type == MarketingEvent.PageView) current.Pages.Add(new MarketingJourneyStepDto { Path = e.Path, AtUtc = e.CreatedAtUtc });
                else if (e.Type == MarketingEvent.ChatStarted) current.StartedChat = true;
            }

            var leads = await _unitOfWork.Repository<PlatformDemoRequest>().Query()
                .Where(r => r.VisitorId == visitorId)
                .OrderBy(r => r.CreatedAtUtc)
                .Select(r => new MarketingVisitorLeadDto { Id = r.Id, FullName = r.FullName, AcademyName = r.AcademyName, Status = r.Status.ToString(), CreatedAtUtc = r.CreatedAtUtc })
                .ToListAsync(cancellationToken);
            var chats = await _unitOfWork.Repository<MarketingChatConversation>().Query()
                .Where(c => c.VisitorId == visitorId)
                .OrderBy(c => c.CreatedAtUtc)
                .Select(c => new MarketingChatConversationDto
                {
                    Id = c.Id, StartedAtUtc = c.CreatedAtUtc, LastMessageAtUtc = c.LastMessageAtUtc, MessageCount = c.MessageCount,
                    FirstMessage = c.FirstMessage, Source = c.Source, Campaign = c.Campaign, LandingPath = c.LandingPath, BookedName = c.BookedName,
                })
                .ToListAsync(cancellationToken);

            var views = events.Where(e => e.Type == MarketingEvent.PageView).ToList();
            return new MarketingVisitorDetailDto
            {
                VisitorId = visitorId,
                Visitor = visitorId.Length > 8 ? visitorId[^8..] : visitorId,
                FirstSeenUtc = events[0].CreatedAtUtc,
                LastSeenUtc = events[^1].CreatedAtUtc,
                Country = events.Select(e => e.Country).LastOrDefault(c => c != null),
                PageViews = views.Count,
                DistinctPages = views.Select(e => e.Path).Distinct().Count(),
                Visits = visits.Where(v => v.Pages.Count > 0 || v.StartedChat).OrderByDescending(v => v.StartUtc).ToList(),
                DemoRequests = leads,
                Chats = chats,
            };
        }

        public async Task<string?> GetStyleSampleAsync(CancellationToken cancellationToken = default)
        {
            var best = (await GetContentPerformanceAsync(90, 0, cancellationToken)).Posts.FirstOrDefault()?.Path;
            var slug = best?["/blog/".Length..];
            var now = DateTime.UtcNow;
            var published = _unitOfWork.Repository<BlogPost>().Query().Where(b => b.IsPublished && b.PublishedAtUtc <= now);
            var sample = slug is null ? null : await published.Where(b => b.Slug == slug).Select(b => b.Content).FirstOrDefaultAsync(cancellationToken);
            return sample ?? await published.OrderByDescending(b => b.PublishedAtUtc).Select(b => b.Content).FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<MarketingSiteBehaviourDto> GetSiteBehaviourAsync(int days, int tzOffsetMinutes, string? country = null,
            CancellationToken cancellationToken = default, DateOnly? from = null, DateOnly? to = null)
        {
            (days, var offset, var startLocal, var endLocal, var startUtc, var endUtc) = Window(days, tzOffsetMinutes, from, to);
            var events = await _unitOfWork.Repository<MarketingEvent>().Query()
                .Where(e => e.CreatedAtUtc >= startUtc && e.CreatedAtUtc < endUtc)
                .OrderBy(e => e.CreatedAtUtc)
                .Select(e => new { e.VisitorId, e.Type, e.Path, e.CreatedAtUtc, e.Device, e.Source, e.Country })
                .ToListAsync(cancellationToken);
            var countryFilter = string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant();
            if (countryFilter is not null)
            {
                var keep = events.GroupBy(e => e.VisitorId)
                    .Where(g => (g.Select(e => e.Country).LastOrDefault(c => c != null) ?? "?") == countryFilter)
                    .Select(g => g.Key).ToHashSet();
                events = events.Where(e => keep.Contains(e.VisitorId)).ToList();
            }
            var booked = (await _unitOfWork.Repository<PlatformDemoRequest>().Query()
                    .Where(r => r.VisitorId != null && r.CreatedAtUtc >= startUtc && r.CreatedAtUtc < endUtc)
                    .Select(r => r.VisitorId!).ToListAsync(cancellationToken))
                .ToHashSet();

            // Split each visitor's page views into visits (30-minute gaps).
            var visits = new List<List<(string Path, DateTime At)>>();
            foreach (var g in events.Where(e => e.Type == MarketingEvent.PageView).GroupBy(e => e.VisitorId))
            {
                List<(string Path, DateTime At)>? current = null;
                foreach (var e in g)
                {
                    if (current is null || e.CreatedAtUtc - current[^1].At > VisitGap) { current = []; visits.Add(current); }
                    current.Add((e.Path, e.CreatedAtUtc));
                }
            }

            var timed = new Dictionary<string, List<double>>();
            var exits = new Dictionary<string, int>();
            foreach (var visit in visits)
            {
                for (var i = 0; i < visit.Count - 1; i++)
                {
                    var seconds = (visit[i + 1].At - visit[i].At).TotalSeconds;
                    if (seconds is > 0 and <= 30 * 60) (timed.TryGetValue(visit[i].Path, out var l) ? l : timed[visit[i].Path] = []).Add(seconds);
                }
                exits[visit[^1].Path] = exits.GetValueOrDefault(visit[^1].Path) + 1;
            }

            var views = events.Where(e => e.Type == MarketingEvent.PageView).ToList();
            var pages = views.GroupBy(e => e.Path).Select(g =>
            {
                var pageViews = g.Count();
                var pageExits = exits.GetValueOrDefault(g.Key);
                return new MarketingPageBehaviourDto
                {
                    Path = g.Key,
                    Visitors = g.Select(e => e.VisitorId).Distinct().Count(),
                    Views = pageViews,
                    Exits = pageExits,
                    ExitRate = Math.Round(pageExits * 100.0 / pageViews, 1),
                    AvgSeconds = timed.TryGetValue(g.Key, out var t) && t.Count > 0 ? (int)Math.Round(t.Average()) : null,
                };
            }).OrderByDescending(p => p.Visitors).ThenByDescending(p => p.Views).Take(25).ToList();

            var byVisitor = views.GroupBy(e => e.VisitorId).ToDictionary(g => g.Key, g => g.Select(e => e.Path).ToHashSet());
            int Reached(Func<HashSet<string>, bool> match) => byVisitor.Count(v => match(v.Value));
            var visitorIds = byVisitor.Keys.ToList();

            return new MarketingSiteBehaviourDto
            {
                Days = days,
                StartDate = startLocal.ToString("yyyy-MM-dd"),
                EndDate = endLocal.ToString("yyyy-MM-dd"),
                Visitors = visitorIds.Count,
                Visits = visits.Count,
                SinglePageVisits = visits.Count(v => v.Count == 1),
                ReturningVisitors = views.GroupBy(e => e.VisitorId).Count(g => g.Select(e => (e.CreatedAtUtc + offset).Date).Distinct().Count() > 1),
                ChatVisitors = events.Where(e => e.Type == MarketingEvent.ChatStarted).Select(e => e.VisitorId).Distinct().Count(),
                BookedVisitors = visitorIds.Count(booked.Contains),
                FormStartedVisitors = events.Where(e => e.Type == MarketingEvent.FormStarted).Select(e => e.VisitorId).Distinct().Count(),
                FormValidationFailedVisitors = events.Where(e => e.Type == MarketingEvent.FormValidationFailed).Select(e => e.VisitorId).Distinct().Count(),
                FormAbandonedVisitors = events.Where(e => e.Type == MarketingEvent.FormAbandoned).Select(e => e.VisitorId).Distinct().Count(),
                FormSubmittedVisitors = events.Where(e => e.Type == MarketingEvent.FormSubmitted).Select(e => e.VisitorId).Distinct().Count(),
                WhatsAppVisitors = events.Where(e => e.Type == MarketingEvent.WhatsAppClicked).Select(e => e.VisitorId).Distinct().Count(),
                Funnel =
                [
                    new() { Name = "Visited the site", Visitors = visitorIds.Count },
                    new() { Name = "Read a blog post or feature page", Visitors = Reached(p => p.Any(x => x.StartsWith("/blog/") || x.StartsWith("/solutions/") || x.StartsWith("/customers"))) },
                    new() { Name = "Looked at pricing", Visitors = Reached(p => p.Contains("/pricing")) },
                    new() { Name = "Opened Get Started or Demo", Visitors = Reached(p => p.Contains("/get-started") || p.Contains("/demo")) },
                    new() { Name = "Started the demo form", Visitors = events.Where(e => e.Type == MarketingEvent.FormStarted).Select(e => e.VisitorId).Distinct().Count() },
                    new() { Name = "Submitted the demo form", Visitors = events.Where(e => e.Type == MarketingEvent.FormSubmitted).Select(e => e.VisitorId).Distinct().Count() },
                    new() { Name = "Booked a demo", Visitors = visitorIds.Count(booked.Contains) },
                ],
                Pages = pages,
                Devices = views.GroupBy(e => e.VisitorId).Select(g => g.First().Device).GroupBy(d => d).Select(d => new NamedCountDto { Name = d.Key, Count = d.Count() }).OrderByDescending(d => d.Count).ToList(),
                Sources = views.GroupBy(e => e.VisitorId).Select(g => SourceLabel(g.First().Source)).GroupBy(s => s).Select(s => new NamedCountDto { Name = s.Key, Count = s.Count() }).OrderByDescending(s => s.Count).Take(8).ToList(),
            };
        }

        private static readonly string[] BuyingPages = ["/pricing", "/get-started", "/demo"];

        public async Task<MarketingContentPerformanceDto> GetContentPerformanceAsync(int days, int tzOffsetMinutes,
            CancellationToken cancellationToken = default, DateOnly? from = null, DateOnly? to = null)
        {
            (days, _, var startLocal, var endLocal, var startUtc, var endUtc) = Window(days, tzOffsetMinutes, from, to);
            var views = await _unitOfWork.Repository<MarketingEvent>().Query()
                .Where(e => e.Type == MarketingEvent.PageView && e.CreatedAtUtc >= startUtc && e.CreatedAtUtc < endUtc)
                .OrderBy(e => e.CreatedAtUtc)
                .Select(e => new { e.VisitorId, e.Path, e.CreatedAtUtc })
                .ToListAsync(cancellationToken);
            var booked = (await _unitOfWork.Repository<PlatformDemoRequest>().Query()
                    .Where(r => r.VisitorId != null && r.CreatedAtUtc >= startUtc && r.CreatedAtUtc < endUtc)
                    .Select(r => r.VisitorId!)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
            var titles = (await _unitOfWork.Repository<BlogPost>().Query().Select(b => new { b.Slug, b.Title }).ToListAsync(cancellationToken))
                .GroupBy(b => b.Slug).ToDictionary(g => g.Key, g => g.First().Title);
            var byVisitor = views.GroupBy(v => v.VisitorId).ToDictionary(g => g.Key, g => g.ToList());

            var posts = views
                .Where(v => v.Path.StartsWith("/blog/"))
                .GroupBy(v => v.Path)
                .Select(g =>
                {
                    var readers = g.Select(v => v.VisitorId).Distinct().ToList();
                    int After(Func<string, bool> match) => readers.Count(r =>
                    {
                        var firstRead = g.Where(v => v.VisitorId == r).Min(v => v.CreatedAtUtc);
                        return byVisitor[r].Any(v => v.CreatedAtUtc > firstRead && match(v.Path));
                    });
                    return new MarketingContentStatDto
                    {
                        Path = g.Key,
                        Title = titles.GetValueOrDefault(g.Key["/blog/".Length..]),
                        Readers = readers.Count,
                        Views = g.Count(),
                        WentOn = After(_ => true),
                        ToBuyingPages = After(p => BuyingPages.Contains(p)),
                        BookedDemo = readers.Count(booked.Contains),
                    };
                })
                .OrderByDescending(p => p.ToBuyingPages).ThenByDescending(p => p.Readers).ThenByDescending(p => p.WentOn)
                .ToList();

            return new MarketingContentPerformanceDto
            {
                Days = days,
                StartDate = startLocal.ToString("yyyy-MM-dd"),
                EndDate = endLocal.ToString("yyyy-MM-dd"),
                BlogReaders = views.Where(v => v.Path.StartsWith("/blog/")).Select(v => v.VisitorId).Distinct().Count(),
                Posts = posts,
            };
        }

        public async Task<MarketingBlogIdeaEvidence> GetBlogIdeaEvidenceAsync(int days, int tzOffsetMinutes,
            CancellationToken cancellationToken = default, DateOnly? from = null, DateOnly? to = null)
        {
            var performance = await GetContentPerformanceAsync(days, tzOffsetMinutes, cancellationToken, from, to);
            var titles = await _unitOfWork.Repository<BlogPost>().Query()
                .OrderByDescending(b => b.CreatedAtUtc).Select(b => b.Title).Take(200).ToListAsync(cancellationToken);
            var since = DateTime.UtcNow.AddDays(-90);
            var questions = await _unitOfWork.Repository<MarketingChatConversation>().Query()
                .Where(c => c.CreatedAtUtc >= since && c.FirstMessage != "")
                .OrderByDescending(c => c.CreatedAtUtc).Select(c => c.FirstMessage).Take(40).ToListAsync(cancellationToken);
            return new MarketingBlogIdeaEvidence
            {
                Performance = performance,
                ExistingTitles = titles,
                VisitorQuestions = questions.Select(RedactContact).Where(q => q.Length > 0).Distinct().ToList(),
            };
        }

        /// <summary>Removes email addresses and phone-like digit runs before visitor text leaves the system.</summary>
        public static string RedactContact(string text)
        {
            var cleaned = EmailPattern().Replace(text, "[email]");
            cleaned = PhonePattern().Replace(cleaned, "[phone]");
            cleaned = cleaned.Trim();
            return cleaned.Length > 200 ? cleaned[..200] : cleaned;
        }

        [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[^\s@]+")]
        private static partial Regex EmailPattern();

        [GeneratedRegex(@"\+?\d[\d\s\-()]{6,}\d")]
        private static partial Regex PhonePattern();

        /// <summary>Drops immediate repeats ("/ → / → /pricing" reads as "/ → /pricing").</summary>
        private static IEnumerable<string> Collapse(IEnumerable<string> paths)
        {
            string? last = null;
            foreach (var p in paths)
            {
                if (p != last) yield return p;
                last = p;
            }
        }

        public async Task<MarketingDashboardDto> GetDashboardAsync(int days, int tzOffsetMinutes, CancellationToken cancellationToken = default, DateOnly? from = null, DateOnly? to = null)
        {
            var nowUtc = DateTime.UtcNow;
            // The previous window is the same length immediately before this one.
            (days, var offset, var startLocal, var endLocal, var startUtc, var endUtc) = Window(days, tzOffsetMinutes, from, to);
            var previousStartUtc = startUtc.AddDays(-days);

            await _unitOfWork.Repository<MarketingEvent>()
                .ExecuteHardDeleteAsync(e => e.CreatedAtUtc < nowUtc - Retention, cancellationToken);

            var events = await _unitOfWork.Repository<MarketingEvent>().Query()
                .Where(e => e.CreatedAtUtc >= previousStartUtc && e.CreatedAtUtc < endUtc)
                .Select(e => new { e.CreatedAtUtc, e.Type, e.Path, e.VisitorId, e.Source, e.Medium, e.Campaign, e.Device, e.Country })
                .ToListAsync(cancellationToken);
            var requests = await _unitOfWork.Repository<PlatformDemoRequest>().Query()
                .Where(r => r.CreatedAtUtc >= previousStartUtc && r.CreatedAtUtc < endUtc)
                .Select(r => new { r.CreatedAtUtc, r.Message, r.Status, r.Source, r.Campaign, r.LandingPath })
                .ToListAsync(cancellationToken);

            var current = events.Where(e => e.CreatedAtUtc >= startUtc).ToList();
            var previous = events.Where(e => e.CreatedAtUtc < startUtc).ToList();
            var currentRequests = requests.Where(r => r.CreatedAtUtc >= startUtc).ToList();
            var previousRequests = requests.Where(r => r.CreatedAtUtc < startUtc).ToList();

            MarketingTotalsDto Totals(
                IEnumerable<(string Type, string VisitorId)> evts,
                IEnumerable<(string? Message, StoreInquiryStatus Status)> demoRequests)
            {
                var list = evts.ToList();
                var requestsList = demoRequests.ToList();
                var visitors = list.Where(e => e.Type == MarketingEvent.PageView).Select(e => e.VisitorId).Distinct().Count();
                return new MarketingTotalsDto
                {
                    PageViews = list.Count(e => e.Type == MarketingEvent.PageView),
                    Visitors = visitors,
                    ChatsStarted = list.Count(e => e.Type == MarketingEvent.ChatStarted),
                    DemoRequests = requestsList.Count,
                    ChatBookings = requestsList.Count(r => r.Message != null && r.Message.StartsWith(ChatBookingMarker)),
                    ConversionRate = visitors == 0 ? 0 : Math.Round(requestsList.Count * 100.0 / visitors, 1),
                    PagesPerVisitor = visitors == 0 ? 0 : Math.Round(list.Count(e => e.Type == MarketingEvent.PageView) * 1.0 / visitors, 1),
                    ChatStartRate = visitors == 0 ? 0 : Math.Round(list.Count(e => e.Type == MarketingEvent.ChatStarted) * 100.0 / visitors, 1),
                    DemoPipelineConversionRate = requestsList.Count == 0 ? 0 : Math.Round(
                        requestsList.Count(r => r.Status == StoreInquiryStatus.Converted) * 100.0 / requestsList.Count, 1),
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
                    ChatsStarted = current.Count(e => e.Type == MarketingEvent.ChatStarted && LocalDay(e.CreatedAtUtc) == day),
                })
                .ToList();

            foreach (var point in daily)
            {
                point.ConversionRate = point.Visitors == 0 ? 0 : Math.Round(point.DemoRequests * 100.0 / point.Visitors, 1);
                point.PagesPerVisitor = point.Visitors == 0 ? 0 : Math.Round(point.PageViews * 1.0 / point.Visitors, 1);
            }

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

            static List<MarketingPerformanceDto> Performance(
                IEnumerable<(string Name, StoreInquiryStatus Status)> leadRows,
                IEnumerable<string> visitorRows)
            {
                var leads = leadRows.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.ToList());
                var visitors = visitorRows.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                return leads.Keys.Union(visitors.Keys).Select(name =>
                {
                    var rows = leads.GetValueOrDefault(name) ?? [];
                    var visitorCount = visitors.GetValueOrDefault(name);
                    var converted = rows.Count(x => x.Status == StoreInquiryStatus.Converted);
                    return new MarketingPerformanceDto
                    {
                        Name = name, Visitors = visitorCount, Leads = rows.Count, Converted = converted,
                        LeadConversionRate = visitorCount == 0 ? 0 : Math.Round(rows.Count * 100.0 / visitorCount, 1),
                        ConversionRate = rows.Count == 0 ? 0 : Math.Round(converted * 100.0 / rows.Count, 1),
                    };
                }).OrderByDescending(x => x.Converted).ThenByDescending(x => x.Leads).Take(10).ToList();
            }

            return new MarketingDashboardDto
            {
                Days = days,
                StartDate = startLocal.ToString("yyyy-MM-dd"),
                EndDate = endLocal.ToString("yyyy-MM-dd"),
                Totals = Totals(current.Select(e => (e.Type, e.VisitorId)), currentRequests.Select(r => (r.Message, r.Status))),
                Previous = Totals(previous.Select(e => (e.Type, e.VisitorId)), previousRequests.Select(r => (r.Message, r.Status))),
                Daily = daily,
                TopPages = Top(views.Select(e => e.Path), 10),
                TopBlogPosts = Top(views.Where(e => e.Path.StartsWith("/blog/")).Select(e => e.Path), 10),
                Sources = Top(firstTouch.Select(e => SourceLabel(e.Source)), 10),
                Campaigns = Top(firstTouch.Where(e => e.Campaign != null).Select(e => e.Campaign!), 10),
                Mediums = Top(firstTouch.Where(e => e.Medium != null).Select(e => e.Medium!), 10),
                Countries = Top(firstTouch.Where(e => e.Country != null).Select(e => e.Country!), 20),
                Devices = Top(firstTouch.Select(e => e.Device), 3),
                DemoRequestsByStatus = Top(currentRequests.Select(r => r.Status.ToString()), 10),
                SourceQuality = Performance(currentRequests.Select(r => (r.Source ?? "Direct", r.Status)), firstTouch.Select(e => SourceLabel(e.Source))),
                CampaignPerformance = Performance(currentRequests.Where(r => r.Campaign != null).Select(r => (r.Campaign!, r.Status)), firstTouch.Where(e => e.Campaign != null).Select(e => e.Campaign!)),
                LandingPagePerformance = Performance(currentRequests.Select(r => (r.LandingPath ?? "Unknown", r.Status)), firstTouch.Select(e => e.Path)),
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

        private static string? CleanCountry(string? country)
        {
            var value = country?.Trim().ToUpperInvariant();
            return value is { Length: 2 } && value.All(char.IsAsciiLetter) ? value : null;
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
