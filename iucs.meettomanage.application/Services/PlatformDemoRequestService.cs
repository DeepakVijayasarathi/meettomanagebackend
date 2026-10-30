using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Dto.Common;
using iucs.meettomanage.application.Mappings;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.application.Services
{
    public class PlatformDemoRequestService : IPlatformDemoRequestService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;

        public PlatformDemoRequestService(IUnitOfWork unitOfWork, IAuditLogService auditLog)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
        }

        public async Task<PlatformDemoRequestDto> CreateAsync(
            CreatePlatformDemoRequestRequest request,
            CancellationToken cancellationToken = default)
        {
            var demoRequest = new PlatformDemoRequest
            {
                Id = Guid.NewGuid(),
                FullName = request.FullName.Trim(),
                WorkEmail = request.WorkEmail.Trim().ToLowerInvariant(),
                Phone = request.Phone.Trim(),
                AcademyName = request.AcademyName.Trim(),
                Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
                VisitorId = Clean(request.VisitorId), Source = NormalizeSource(request.Source), Medium = NormalizeToken(request.Medium),
                Campaign = NormalizeToken(request.Campaign), LandingPath = Clean(request.LandingPath),
            };
            await _unitOfWork.Repository<PlatformDemoRequest>().AddAsync(demoRequest, cancellationToken);
            await AddActivity(demoRequest.Id, "Created", "Lead submitted.", cancellationToken);
            await _auditLog.StageAsync(AuditAction.Create, nameof(PlatformDemoRequest), demoRequest.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return demoRequest.ToDto();
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
            var duplicates = requests.GroupBy(r => r.WorkEmail.ToLower()).Where(g => g.Count() > 1).SelectMany(g => g).Select(r => r.Id)
                .Concat(requests.GroupBy(r => r.Phone).Where(g => g.Count() > 1).SelectMany(g => g).Select(r => r.Id)).ToHashSet();
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
            var engagement = await _unitOfWork.Repository<MarketingEvent>().Query().Where(x => visitorIds.Contains(x.VisitorId))
                .GroupBy(x => x.VisitorId).Select(g => new { VisitorId = g.Key, Views = g.Count(x => x.Type == MarketingEvent.PageView), Chats = g.Count(x => x.Type == MarketingEvent.ChatStarted) }).ToDictionaryAsync(x => x.VisitorId, cancellationToken);
            var duplicates = await _unitOfWork.Repository<PlatformDemoRequest>().Query().Where(x => rows.Select(r => r.WorkEmail).Contains(x.WorkEmail) || rows.Select(r => r.Phone).Contains(x.Phone)).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                row.IsDuplicate = duplicates.Count(x => x.Id != row.Id && (x.WorkEmail == row.WorkEmail || x.Phone == row.Phone)) > 0;
                if (row.VisitorId is not null && engagement.TryGetValue(row.VisitorId, out var e)) row.EngagementScoreBoost = Math.Min(25, e.Views * 3 + e.Chats * 10);
            }
        }

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
