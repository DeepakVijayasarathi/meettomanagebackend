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
            return requests.Select(r => r.ToDto()).ToList();
        }

        public async Task<PlatformDemoRequestDto> UpdateStatusAsync(
            Guid id,
            UpdatePlatformDemoRequestStatusRequest request,
            CancellationToken cancellationToken = default)
        {
            var demoRequest = await _unitOfWork.Repository<PlatformDemoRequest>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformDemoRequest), id);

            demoRequest.Status = request.Status;
            await _auditLog.StageAsync(AuditAction.Update, nameof(PlatformDemoRequest), demoRequest.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return demoRequest.ToDto();
        }
    }
}
