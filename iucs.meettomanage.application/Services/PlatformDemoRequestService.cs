using System.Net;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Dto.Common;
using iucs.meettomanage.application.Mappings;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Entities.Settings;
using iucs.meettomanage.domain.Entities.Users;
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
            var ownerEmail = await IsOnAsync(LeadLifecycle.AutoAssign, cancellationToken) ? await AssignOwnerAsync(demoRequest, cancellationToken) : null;
            await _unitOfWork.Repository<PlatformDemoRequest>().AddAsync(demoRequest, cancellationToken);
            await AddActivity(demoRequest.Id, "Created", "Lead submitted.", cancellationToken);
            if (demoRequest.AssignedTo is not null)
                await AddActivity(demoRequest.Id, LeadLifecycle.AutomationActivity, $"Assigned to {demoRequest.AssignedTo}.", cancellationToken);
            await _auditLog.StageAsync(AuditAction.Create, nameof(PlatformDemoRequest), demoRequest.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await SendLeadAlertAsync(demoRequest, ownerEmail, cancellationToken);
            await AcknowledgeAsync(demoRequest, cancellationToken);
            return demoRequest.ToDto();
        }

        /// <summary>An automation is on unless the team switched it off in the console (stored as "false").</summary>
        private async Task<bool> IsOnAsync(string key, CancellationToken cancellationToken)
        {
            var value = await _unitOfWork.Repository<AppSetting>().Query().Where(x => x.Key == key).Select(x => x.Value).FirstOrDefaultAsync(cancellationToken);
            return !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        private string SiteUrl => (_configuration?["Marketing:SiteUrl"] ?? "https://meettomanage.cloud").TrimEnd('/');

        /// <summary>
        /// Round-robin by load: a returning person keeps the owner of their open lead; otherwise the active marketing
        /// user with the fewest open leads gets it. Returns that owner's e-mail for the alert, or null when nobody can own it.
        /// </summary>
        private async Task<string?> AssignOwnerAsync(PlatformDemoRequest lead, CancellationToken cancellationToken)
        {
            // Viewers are read-only in the CRM, so they never own leads.
            var viewers = _unitOfWork.Repository<CrmTeamMember>().Query().Where(x => x.Role == CrmRole.Viewer).Select(x => x.UserId);
            var team = await _unitOfWork.Repository<User>().Query()
                .Where(x => x.Role == UserRole.Marketing && x.Status == UserStatus.Active && !viewers.Contains(x.Id))
                .Select(x => new { x.Id, x.FirstName, x.LastName, x.Email }).ToListAsync(cancellationToken);
            if (team.Count == 0) return null;
            var teamIds = team.Select(x => x.Id).ToList();
            var open = _unitOfWork.Repository<PlatformDemoRequest>().Query()
                .Where(x => x.ArchivedAtUtc == null && x.Stage != LeadStage.Customer && x.Stage != LeadStage.Lost && x.AssignedToUserId != null && teamIds.Contains(x.AssignedToUserId!.Value));

            Guid? ownerId = null;
            if (!string.IsNullOrWhiteSpace(lead.WorkEmail) || !string.IsNullOrWhiteSpace(lead.Phone))
                ownerId = await open.Where(x => (lead.WorkEmail != "" && x.WorkEmail == lead.WorkEmail) || (lead.Phone != "" && x.Phone == lead.Phone))
                    .OrderByDescending(x => x.CreatedAtUtc).Select(x => x.AssignedToUserId).FirstOrDefaultAsync(cancellationToken);
            if (ownerId is null)
            {
                var load = await open.GroupBy(x => x.AssignedToUserId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
                ownerId = team.OrderBy(u => load.FirstOrDefault(l => l.Key == u.Id)?.Count ?? 0).ThenBy(u => u.FirstName).First().Id;
            }
            var owner = team.First(x => x.Id == ownerId);
            lead.AssignedToUserId = owner.Id;
            lead.AssignedTo = $"{owner.FirstName} {owner.LastName}".Trim();
            return owner.Email;
        }

        /// <summary>Best effort "we got your request" e-mail; the lead is already saved, so a failure is only logged.</summary>
        private async Task AcknowledgeAsync(PlatformDemoRequest lead, CancellationToken cancellationToken)
        {
            if (_email is null || !LooksLikeEmail(lead.WorkEmail) || !await IsOnAsync(LeadLifecycle.AutoAcknowledge, cancellationToken)) return;
            try
            {
                var bookingUrl = await _unitOfWork.Repository<AppSetting>().Query().Where(x => x.Key == LeadLifecycle.BookingUrl).Select(x => x.Value).FirstOrDefaultAsync(cancellationToken);
                var mail = LeadEmails.Acknowledgement(lead, SiteUrl, lead.AssignedTo?.Split(' ')[0], Uri.TryCreate(bookingUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? bookingUrl : null);
                await _email.SendAsync(lead.WorkEmail, mail.Subject, mail.Html, cancellationToken);
                var tracked = await _unitOfWork.Repository<PlatformDemoRequest>().TrackedQuery().FirstAsync(x => x.Id == lead.Id, cancellationToken);
                tracked.AcknowledgedAtUtc = DateTime.UtcNow;
                await AddActivity(lead.Id, LeadLifecycle.AutomationActivity, "Acknowledgement e-mail sent.", cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Couldn't send the acknowledgement e-mail for demo request {Id}", lead.Id);
            }
        }

        public static bool LooksLikeEmail(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

        public async Task<PlatformDemoRequestDto> SetStageAsync(Guid id, SetLeadStageRequest request, CancellationToken cancellationToken = default)
        {
            var lead = await _unitOfWork.Repository<PlatformDemoRequest>().TrackedQuery().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformDemoRequest), id);
            var now = DateTime.UtcNow;
            var note = Clean(request.Note);
            if (request.Stage == LeadStage.DemoBooked)
            {
                if (request.DemoAtUtc is not { } at) throw new DomainValidationException("Choose the demo date and time.");
                if (at < now.AddMinutes(-5)) throw new DomainValidationException("The demo time is in the past.");
                var rescheduled = lead.Stage == LeadStage.DemoBooked && lead.DemoAtUtc != at;
                lead.DemoAtUtc = DateTime.SpecifyKind(at, DateTimeKind.Utc);
                lead.DemoReminderDaySentAtUtc = lead.DemoReminderHourSentAtUtc = null;
                LeadLifecycle.Apply(lead, LeadStage.DemoBooked, now);
                lead.StageChangedAtUtc = now; // also restarts the reminder clock on a reschedule
                var when = LeadEmails.FormatDemoTime(lead.DemoAtUtc.Value, TimeZoneId);
                await AddActivity(id, "Stage", $"{(rescheduled ? "Demo moved to" : "Demo booked for")} {when}.", cancellationToken);
                if (await IsOnAsync(LeadLifecycle.DemoReminders, cancellationToken))
                    await SendToLeadAsync(lead, LeadEmails.DemoConfirmation(lead, when), "Demo confirmation e-mail sent.", cancellationToken);
            }
            else if (request.Stage == LeadStage.Lost)
            {
                var reason = Clean(request.LostReason) ?? throw new DomainValidationException("Say why the lead was lost.");
                LeadLifecycle.Apply(lead, LeadStage.Lost, now);
                lead.LostReason = reason[..Math.Min(reason.Length, 200)];
                await AddActivity(id, "Stage", $"Marked lost: {lead.LostReason}", cancellationToken);
            }
            else
            {
                LeadLifecycle.Apply(lead, request.Stage, now);
                await AddActivity(id, "Stage", $"Moved to {LeadLifecycle.Label(request.Stage)}.", cancellationToken);
                if (request.Stage == LeadStage.DemoDone && lead.DemoRecapSentAtUtc is null && await IsOnAsync(LeadLifecycle.DemoRecap, cancellationToken))
                {
                    if (await SendToLeadAsync(lead, LeadEmails.DemoRecap(lead, SiteUrl, request.RecapNote), "Demo recap e-mail sent with the trial link.", cancellationToken))
                        lead.DemoRecapSentAtUtc = now;
                }
            }
            if (note is not null) await AddActivity(id, "Note", note[..Math.Min(note.Length, 1000)], cancellationToken);
            await _auditLog.StageAsync(AuditAction.Update, nameof(PlatformDemoRequest), id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return lead.ToDto();
        }

        private string TimeZoneId => _configuration?["Marketing:TimeZone"] ?? "Asia/Kolkata";

        /// <summary>Sends one lifecycle e-mail to the lead and records it. False when there's no address or sending failed.</summary>
        private async Task<bool> SendToLeadAsync(PlatformDemoRequest lead, LeadEmails.Email mail, string activity, CancellationToken cancellationToken)
        {
            if (_email is null || !LooksLikeEmail(lead.WorkEmail)) return false;
            try
            {
                await _email.SendAsync(lead.WorkEmail, mail.Subject, mail.Html, cancellationToken);
                await AddActivity(lead.Id, LeadLifecycle.AutomationActivity, activity, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Couldn't send a lifecycle e-mail for demo request {Id}", lead.Id);
                return false;
            }
        }

        /// <summary>
        /// Emails the marketing team about a new lead. Marketing:LeadAlertEmail holds one or more
        /// addresses separated by commas or semicolons (blank = off); each gets its own email.
        /// Best effort: the lead is already saved, so a mail failure is only logged.
        /// </summary>
        private async Task SendLeadAlertAsync(PlatformDemoRequest lead, string? ownerEmail, CancellationToken cancellationToken)
        {
            // The assigned owner hears about their new lead too, alongside the configured team inbox.
            var recipients = (_configuration?["Marketing:LeadAlertEmail"] ?? "")
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append(ownerEmail ?? "").Where(LooksLikeEmail)
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

            SyncStatus(demoRequest, request.Status);
            await AddActivity(id, "Status", $"Status changed to {request.Status}.", cancellationToken);
            await _auditLog.StageAsync(AuditAction.Update, nameof(PlatformDemoRequest), demoRequest.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return demoRequest.ToDto();
        }

        public async Task<PlatformDemoRequestDto> UpdateAsync(Guid id, UpdatePlatformDemoRequestRequest request, CancellationToken cancellationToken = default)
        {
            var lead = await _unitOfWork.Repository<PlatformDemoRequest>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformDemoRequest), id);
            if (request.Status.HasValue) SyncStatus(lead, request.Status.Value);
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

        /// <summary>The older status screens still set Status directly; keep the lifecycle stage and response time in step.</summary>
        private static void SyncStatus(PlatformDemoRequest lead, StoreInquiryStatus status)
        {
            var now = DateTime.UtcNow;
            var stage = LeadLifecycle.StageForStatus(status, lead.Stage);
            if (stage != lead.Stage) LeadLifecycle.Apply(lead, stage, now);
            lead.Status = status;
            if (status == StoreInquiryStatus.Contacted) { lead.LastContactedAtUtc = now; lead.FirstContactedAtUtc ??= now; }
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
            if (request.Stage.HasValue) query = query.Where(x => x.Stage == request.Stage);
            if (request.VisibleToUserId is { } me) query = query.Where(x => x.AssignedToUserId == null || x.AssignedToUserId == me);
            if (request.UnassignedOnly) query = query.Where(x => x.AssignedToUserId == null);
            if (!string.IsNullOrWhiteSpace(request.Region)) query = query.Where(x => x.Region == request.Region.Trim());
            if (!string.IsNullOrWhiteSpace(request.Segment)) query = query.Where(x => x.Segment == request.Segment.Trim());
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
                if (request.Status.HasValue) SyncStatus(lead, request.Status.Value);
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

        public async Task<MarketingLeadActivityDto> AddActivityAsync(Guid id, CreateMarketingLeadActivityRequest request, CancellationToken cancellationToken = default)
        {
            _ = await _unitOfWork.Repository<PlatformDemoRequest>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformDemoRequest), id);
            var type = Clean(request.Type) ?? "Note";
            var detail = Clean(request.Detail) ?? "Activity recorded.";
            var activity = new MarketingLeadActivity
            {
                Id = Guid.NewGuid(), PlatformDemoRequestId = id,
                Type = type[..Math.Min(type.Length, 50)],
                Detail = detail[..Math.Min(detail.Length, 1000)],
            };
            await _unitOfWork.Repository<MarketingLeadActivity>().AddAsync(activity, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return new MarketingLeadActivityDto { Id = activity.Id, Type = activity.Type, Detail = activity.Detail, CreatedAtUtc = activity.CreatedAtUtc };
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
