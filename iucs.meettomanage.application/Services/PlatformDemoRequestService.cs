using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Dto.Marketing;
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
                FullName = request.FullName.Trim(),
                WorkEmail = request.WorkEmail.Trim().ToLowerInvariant(),
                Phone = request.Phone.Trim(),
                AcademyName = request.AcademyName.Trim(),
                Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
                VisitorId = Clean(request.VisitorId), Source = Clean(request.Source), Medium = Clean(request.Medium),
                Campaign = Clean(request.Campaign), LandingPath = Clean(request.LandingPath),
            };
            await _unitOfWork.Repository<PlatformDemoRequest>().AddAsync(demoRequest, cancellationToken);
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
            await _auditLog.StageAsync(AuditAction.Update, nameof(PlatformDemoRequest), id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return lead.ToDto();
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var demoRequest = await _unitOfWork.Repository<PlatformDemoRequest>()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformDemoRequest), id);

            _unitOfWork.Repository<PlatformDemoRequest>().Remove(demoRequest);
            await _auditLog.StageAsync(
                AuditAction.Delete,
                nameof(PlatformDemoRequest),
                demoRequest.Id.ToString(),
                cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
