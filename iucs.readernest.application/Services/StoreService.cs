using iucs.readernest.application.Common.Exceptions;
using iucs.readernest.application.Dto.Admission;
using iucs.readernest.application.Mappings;
using iucs.readernest.domain.Entities.Admission;
using iucs.readernest.domain.Entities.Billing;
using iucs.readernest.domain.Enums;
using iucs.readernest.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.readernest.application.Services
{
    public class StoreService : IStoreService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;

        public StoreService(IUnitOfWork unitOfWork, IAuditLogService auditLog)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
        }

        public async Task<IReadOnlyList<StorePlanDto>> ListPublicPlansAsync(CancellationToken cancellationToken = default)
        {
            var plans = await _unitOfWork.Repository<PackagePlan>().Query()
                .Include(p => p.Course)
                .Where(p => p.IsActive)
                .OrderBy(p => p.Name)
                .ToListAsync(cancellationToken);
            return plans.Select(p => p.ToStoreDto()).ToList();
        }

        public async Task<StoreInquiryDto> CreateInquiryAsync(
            CreateStoreInquiryRequest request,
            CancellationToken cancellationToken = default)
        {
            var plan = await _unitOfWork.Repository<PackagePlan>()
                .FirstOrDefaultAsync(p => p.Id == request.PackagePlanId && p.IsActive, cancellationToken)
                ?? throw new NotFoundException("That plan is no longer available.");

            var inquiry = new StoreInquiry
            {
                PackagePlanId = plan.Id,
                ParentName = request.ParentName.Trim(),
                ParentEmail = request.ParentEmail.Trim().ToLowerInvariant(),
                ParentPhone = request.ParentPhone.Trim(),
                ChildName = request.ChildName.Trim(),
                ChildAge = request.ChildAge,
                Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            };
            await _unitOfWork.Repository<StoreInquiry>().AddAsync(inquiry, cancellationToken);
            await _auditLog.StageAsync(AuditAction.Create, nameof(StoreInquiry), inquiry.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            inquiry.PackagePlan = plan;
            return inquiry.ToDto();
        }

        public async Task<IReadOnlyList<StoreInquiryDto>> ListInquiriesAsync(
            StoreInquiryStatus? status,
            CancellationToken cancellationToken = default)
        {
            IQueryable<StoreInquiry> query = _unitOfWork.Repository<StoreInquiry>().Query().Include(i => i.PackagePlan);
            if (status.HasValue)
            {
                query = query.Where(i => i.Status == status.Value);
            }

            var inquiries = await query.OrderByDescending(i => i.CreatedAtUtc).ToListAsync(cancellationToken);
            return inquiries.Select(i => i.ToDto()).ToList();
        }

        public async Task<StoreInquiryDto> UpdateInquiryStatusAsync(
            Guid id,
            UpdateStoreInquiryStatusRequest request,
            CancellationToken cancellationToken = default)
        {
            // Load tracked (Query()/BaseQuery is AsNoTracking; mutating that never persists).
            var inquiry = await _unitOfWork.Repository<StoreInquiry>().FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(StoreInquiry), id);

            inquiry.Status = request.Status;
            await _auditLog.StageAsync(AuditAction.Update, nameof(StoreInquiry), inquiry.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var withPlan = await _unitOfWork.Repository<StoreInquiry>().Query()
                .Include(i => i.PackagePlan)
                .FirstAsync(i => i.Id == id, cancellationToken);
            return withPlan.ToDto();
        }
    }
}
