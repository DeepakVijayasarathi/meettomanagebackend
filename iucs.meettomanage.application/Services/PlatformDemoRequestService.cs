using System.Net;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Dto.Common;
using iucs.meettomanage.application.Mappings;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace iucs.meettomanage.application.Services
{
    public class PlatformDemoRequestService : IPlatformDemoRequestService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;
        private readonly IEmailSender? _email;
        private readonly IConfiguration? _configuration;
        private readonly ILogger<PlatformDemoRequestService>? _logger;

        public PlatformDemoRequestService(
            IUnitOfWork unitOfWork,
            IAuditLogService auditLog,
            IEmailSender? email = null,
            IConfiguration? configuration = null,
            ILogger<PlatformDemoRequestService>? logger = null)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
            _email = email;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<PlatformDemoRequestDto> CreateAsync(
            CreatePlatformDemoRequestRequest request,
            CancellationToken cancellationToken = default)
        {
            var demoRequest = new PlatformDemoRequest
            {
                Id = Guid.NewGuid(),
                FullName = request.FullName.Trim(),
                WorkEmail = request.WorkEmail?.Trim().ToLowerInvariant() ?? "",
                Phone = request.Phone?.Trim() ?? "",
                AcademyName = string.IsNullOrWhiteSpace(request.AcademyName) ? "Not provided" : request.AcademyName.Trim(),
                Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
                VisitorId = Clean(request.VisitorId), Source = NormalizeSource(request.Source), Medium = NormalizeToken(request.Medium),
                Campaign = NormalizeToken(request.Campaign), LandingPath = Clean(request.LandingPath),
            };
            await _unitOfWork.Repository<PlatformDemoRequest>().AddAsync(demoRequest, cancellationToken);
            await AddActivity(demoRequest.Id, "Created", "Lead submitted.", cancellationToken);
            await _auditLog.StageAsync(AuditAction.Create, nameof(PlatformDemoRequest), demoRequest.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await SendLeadAlertAsync(demoRequest, cancellationToken);
            return demoRequest.ToDto();
        }

        /// <summary>
        /// Emails the marketing team about a new lead. Marketing:LeadAlertEmail holds one or more
        /// addresses separated by commas or semicolons (blank = off); each gets its own email.
        /// Best effort: the lead is already saved, so a mail failure is only logged.
        /// </summary>
        private async Task SendLeadAlertAsync(PlatformDemoRequest lead, CancellationToken cancellationToken)
        {
            var recipients = (_configuration?["Marketing:LeadAlertEmail"] ?? "")
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (_email is null || recipients.Count == 0) return;
            static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
            var consoleUrl = $"{(_configuration?["Marketing:SiteUrl"] ?? "https://meettomanage.cloud").TrimEnd('/')}/marketing/demo-requests";
            var rows = new (string Label, string? Value)[]
            {
                ("Name", lead.FullName), ("Academy", lead.AcademyName), ("Email", lead.WorkEmail), ("Phone", lead.Phone),
                ("Message", lead.Message), ("Source", lead.Source ?? "Direct"), ("Campaign", lead.Campaign), ("Landing page", lead.LandingPath),
            };
            var table = string.Concat(rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).Select(r =>
                $"<tr><td style=\"padding:4px 12px 4px 0;color:#57534e\">{H(r.Label)}</td><td style=\"padding:4px 0\"><b>{H(r.Value)}</b></td></tr>"));
            var body = $"<p>A new platform demo request just came in.</p><table>{table}</table>" +
                $"<p><a href=\"{H(consoleUrl)}\">Open it in the Marketing console</a></p>";
            var subject = $"New demo request: {lead.FullName} ({lead.AcademyName})";
            // One send per address: a bad address doesn't stop the others getting the alert.
            foreach (var to in recipients)
            {
                try
                {
                    await _email.SendAsync(to, subject, body, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "Couldn't email the lead alert for demo request {Id} to {To}", lead.Id, to);
                }
            }
        }

        public async Task<IReadOnlyList<PlatformDemoRequestDto>> ListAsync(
            StoreInquiryStatus? status,
            CancellationToken cancellationToken = default)
        {
            IQueryable<PlatformDemoRequest> query = _unitOfWork.Repository<PlatformDemoRequest>().Query();
            if (status.HasValue)
            {
                query = query.Where(r => r.Status == status.Value);
            }

            var requests = await query.OrderByDescending(r => r.CreatedAtUtc).ToListAsync(cancellationToken);
            var duplicates = requests.Where(r => !string.IsNullOrWhiteSpace(r.WorkEmail)).GroupBy(r => r.WorkEmail.ToLower()).Where(g => g.Count() > 1).SelectMany(g => g).Select(r => r.Id)
                .Concat(requests.Where(r => !string.IsNullOrWhiteSpace(r.Phone)).GroupBy(r => r.Phone).Where(g => g.Count() > 1).SelectMany(g => g).Select(r => r.Id)).ToHashSet();
            return requests.Select(r => { var dto = r.ToDto(); dto.IsDuplicate = duplicates.Contains(r.Id); return dto; }).ToList();
        }

        public async Task<PlatformDemoRequestDto> UpdateStatusAsync(
            Guid id,
            UpdatePlatformDemoRequestStatusRequest request,
            CancellationToken cancellationToken = default)
        {
            var demoRequest = await _unitOfWork.Repository<PlatformDemoRequest>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformDemoRequest), id);

            demoRequest.Status = request.Status;
            if (request.Status == StoreInquiryStatus.Contacted) demoRequest.LastContactedAtUtc = DateTime.UtcNow;
            await AddActivity(id, "Status", $"Status changed to {request.Status}.", cancellationToken);
            await _auditLog.StageAsync(AuditAction.Update, nameof(PlatformDemoRequest), demoRequest.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return demoRequest.ToDto();
        }

        public async Task<PlatformDemoRequestDto> UpdateAsync(Guid id, UpdatePlatformDemoRequestRequest request, CancellationToken cancellationToken = default)
        {
            var lead = await _unitOfWork.Repository<PlatformDemoRequest>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformDemoRequest), id);
            if (request.Status.HasValue) { lead.Status = request.Status.Value; if (request.Status == StoreInquiryStatus.Contacted) lead.LastContactedAtUtc = DateTime.UtcNow; }
            // This endpoint is patch-like: omitted fields must not erase existing lead data.
            if (request.AssignedTo is not null) lead.AssignedTo = Clean(request.AssignedTo);
            if (request.NextFollowUpAtUtc.HasValue) lead.NextFollowUpAtUtc = request.NextFollowUpAtUtc;
            if (request.InternalNotes is not null) lead.InternalNotes = Clean(request.InternalNotes);
            if (request.AssignedToUserId.HasValue) lead.AssignedToUserId = request.AssignedToUserId;
            await AddActivity(id, "Updated", "Lead assignment, notes or follow-up changed.", cancellationToken);
            await _auditLog.StageAsync(AuditAction.Update, nameof(PlatformDemoRequest), id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return lead.ToDto();
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        private static string? NormalizeToken(string? value) => Clean(value)?.ToLowerInvariant();
        private static string? NormalizeSource(string? value) => Clean(value) is { } source ? MarketingAnalyticsService.SourceLabel(source) : null;

        public async Task<PagedResult<PlatformDemoRequestDto>> ListPagedAsync(MarketingLeadQuery request, CancellationToken cancellationToken = default)
        {
            var page = Math.Max(1, request.Page); var size = Math.Clamp(request.PageSize, 10, 100);
            var query = _unitOfWork.Repository<PlatformDemoRequest>().Query();
            query = request.ArchivedOnly ? query.Where(x => x.ArchivedAtUtc != null) : query.Where(x => x.ArchivedAtUtc == null);
            if (request.Status.HasValue) query = query.Where(x => x.Status == request.Status);
            if (!string.IsNullOrWhiteSpace(request.Source)) query = query.Where(x => x.Source == request.Source);
            if (request.AssignedToUserId.HasValue) query = query.Where(x => x.AssignedToUserId == request.AssignedToUserId);
            if (request.OverdueOnly) query = query.Where(x => x.NextFollowUpAtUtc < DateTime.UtcNow && x.Status != StoreInquiryStatus.Converted && x.Status != StoreInquiryStatus.Closed);
            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                var search = request.Search.Trim().ToLower();
                query = query.Where(x => x.FullName.ToLower().Contains(search) || x.WorkEmail.ToLower().Contains(search) || x.Phone.Contains(search) || x.AcademyName.ToLower().Contains(search));
            }
            var total = await query.CountAsync(cancellationToken);
            var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
            await Enrich(rows, cancellationToken);
            return new PagedResult<PlatformDemoRequestDto> { Items = rows.Select(x => x.ToDto()).ToList(), TotalCount = total, Page = page, PageSize = size };
        }

        public async Task<MarketingLeadDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var lead = await _unitOfWork.Repository<PlatformDemoRequest>().Query().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformDemoRequest), id);
            await Enrich([lead], cancellationToken);
            var activity = await _unitOfWork.Repository<MarketingLeadActivity>().Query().Where(x => x.PlatformDemoRequestId == id).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken);
            return new MarketingLeadDetailDto { Lead = lead.ToDto(), Activity = activity.Select(x => new MarketingLeadActivityDto { Id = x.Id, Type = x.Type, Detail = x.Detail, CreatedAtUtc = x.CreatedAtUtc }).ToList() };
        }

        public async Task BulkUpdateAsync(BulkMarketingLeadRequest request, CancellationToken cancellationToken = default)
        {
            var rows = await _unitOfWork.Repository<PlatformDemoRequest>().TrackedQuery().Where(x => request.Ids.Contains(x.Id)).ToListAsync(cancellationToken);
            foreach (var lead in rows)
            {
                if (request.Status.HasValue) lead.Status = request.Status.Value;
                if (request.AssignedToUserId.HasValue) lead.AssignedToUserId = request.AssignedToUserId;
                if (request.NextFollowUpAtUtc.HasValue) lead.NextFollowUpAtUtc = request.NextFollowUpAtUtc;
                if (request.Archive) lead.ArchivedAtUtc = DateTime.UtcNow;
                await AddActivity(lead.Id, request.Archive ? "Archived" : "Bulk update", "Lead changed in a bulk action.", cancellationToken);
            }
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task RestoreAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var lead = await _unitOfWork.Repository<PlatformDemoRequest>().TrackedQuery().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformDemoRequest), id);
            lead.ArchivedAtUtc = null;
            await AddActivity(id, "Restored", "Lead restored to active marketing lists.", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        private async Task Enrich(List<PlatformDemoRequest> rows, CancellationToken cancellationToken)
        {
            var visitorIds = rows.Where(x => x.VisitorId != null).Select(x => x.VisitorId!).Distinct().ToList();
            var journeys = (await _unitOfWork.Repository<MarketingEvent>().Query().Where(x => visitorIds.Contains(x.VisitorId))
                    .Select(x => new JourneyEvent(x.VisitorId, x.Type, x.Path, x.CreatedAtUtc)).ToListAsync(cancellationToken))
                .GroupBy(x => x.VisitorId).ToDictionary(g => g.Key, g => g.ToList());
            var emails = rows.Select(r => r.WorkEmail).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            var phones = rows.Select(r => r.Phone).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            var duplicates = await _unitOfWork.Repository<PlatformDemoRequest>().Query().Where(x =>
                (!string.IsNullOrEmpty(x.WorkEmail) && emails.Contains(x.WorkEmail)) ||
                (!string.IsNullOrEmpty(x.Phone) && phones.Contains(x.Phone))).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                row.IsDuplicate = duplicates.Count(x => x.Id != row.Id &&
                    ((!string.IsNullOrWhiteSpace(row.WorkEmail) && x.WorkEmail == row.WorkEmail) ||
                     (!string.IsNullOrWhiteSpace(row.Phone) && x.Phone == row.Phone))) > 0;
                if (row.VisitorId is not null && journeys.TryGetValue(row.VisitorId, out var events))
                    (row.EngagementScoreBoost, row.EngagementReasons) = JourneyScore(events, row.CreatedAtUtc);
            }
        }

        private sealed record JourneyEvent(string VisitorId, string Type, string Path, DateTime AtUtc);

        private static readonly string[] BuyingPages = ["/pricing", "/get-started", "/demo"];

        /// <summary>
        /// Up to 40 points from what the lead did on the website (before and after submitting):
        /// studying buying pages, coming back on other days, exploring several pages, and chatting.
        /// A visitor who only reloaded the home page earns little; one who compared pricing twice earns a lot.
        /// </summary>
        public static (int Points, List<string> Reasons) JourneyScore(IEnumerable<(string Type, string Path, DateTime AtUtc)> events, DateTime leadCreatedUtc)
        {
            var list = events.ToList();
            var views = list.Where(e => e.Type == MarketingEvent.PageView).ToList();
            var reasons = new List<string>();
            var points = 0;

            var buying = views.Select(v => v.Path).Where(p => BuyingPages.Contains(p)).Distinct().ToList();
            if (buying.Contains("/pricing")) { points += 12; reasons.Add("Looked at pricing"); }
            var otherBuying = buying.Count(p => p != "/pricing");
            if (otherBuying > 0) { points += 6; reasons.Add("Visited the demo pages"); }

            var days = views.Select(v => v.AtUtc.Date).Distinct().Count();
            if (days > 1) { points += Math.Min(16, (days - 1) * 8); reasons.Add($"Came back on {days - 1} other day{(days > 2 ? "s" : "")}"); }

            var pages = views.Select(v => v.Path).Distinct().Count();
            if (pages >= 4) { points += 6; reasons.Add($"Explored {pages} different pages"); }
            else if (pages >= 2) points += 2;

            if (list.Any(e => e.Type == MarketingEvent.ChatStarted)) { points += 10; reasons.Add("Used the website chat"); }

            if (views.Any(v => v.AtUtc > leadCreatedUtc.AddMinutes(30))) { points += 6; reasons.Add("Returned to the site after requesting a demo"); }

            return (Math.Min(40, points), reasons);
        }

        private static (int, List<string>) JourneyScore(List<JourneyEvent> events, DateTime leadCreatedUtc) =>
            JourneyScore(events.Select(e => (e.Type, e.Path, e.AtUtc)), leadCreatedUtc);

        private Task AddActivity(Guid leadId, string type, string detail, CancellationToken cancellationToken) =>
            _unitOfWork.Repository<MarketingLeadActivity>().AddAsync(new MarketingLeadActivity { Id = Guid.NewGuid(), PlatformDemoRequestId = leadId, Type = type, Detail = detail }, cancellationToken);

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var demoRequest = await _unitOfWork.Repository<PlatformDemoRequest>()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformDemoRequest), id);

            _unitOfWork.Repository<PlatformDemoRequest>().Remove(demoRequest);
            await AddActivity(id, "Deleted", "Lead removed from active use.", cancellationToken);
            await _auditLog.StageAsync(
                AuditAction.Delete,
                nameof(PlatformDemoRequest),
                demoRequest.Id.ToString(),
                cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
