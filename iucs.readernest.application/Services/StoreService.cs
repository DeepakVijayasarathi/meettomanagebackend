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
        // Demos booked publicly are always this length — the form only asks for a start
        // time, never a duration, so there is nothing client-controlled to trust here.
        private const int DemoDurationMinutes = 30;
        // Guardrails on an endpoint anyone on the internet can call: enough lead time for
        // a teacher to actually be auto-assigned and notified, and a window short enough
        // that the slot picker stays meaningful (not an open-ended spam surface).
        private static readonly TimeSpan MinLeadTime = TimeSpan.FromHours(2);
        private const int MaxLeadDays = 30;

        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;
        private readonly IDemoBookingService _demoBookingService;

        public StoreService(IUnitOfWork unitOfWork, IAuditLogService auditLog, IDemoBookingService demoBookingService)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
            _demoBookingService = demoBookingService;
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

        public async Task<StoreDemoBookingConfirmationDto> BookDemoAsync(
            CreateStoreDemoBookingRequest request,
            CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            if (request.PreferredStartAtUtc < now + MinLeadTime)
            {
                throw new DomainValidationException(
                    $"Please pick a time at least {MinLeadTime.TotalHours:0} hours from now, so we can line up a teacher.");
            }

            if (request.PreferredStartAtUtc > now.AddDays(MaxLeadDays))
            {
                throw new DomainValidationException($"Please pick a time within the next {MaxLeadDays} days.");
            }

            // Delegates to the same booking logic the admission team's own scheduler uses
            // (auto-assign, session creation, confirmation email) — a public visitor never
            // gets to name a specific teacher or invite extra participants, unlike that flow.
            var booking = await _demoBookingService.CreateAsync(
                new CreateDemoBookingRequest
                {
                    ParentName = request.ParentName.Trim(),
                    ParentEmail = request.ParentEmail.Trim(),
                    ParentPhone = request.ParentPhone.Trim(),
                    ChildName = request.ChildName.Trim(),
                    ChildAge = request.ChildAge,
                    Department = request.Department,
                    TeacherProfileId = null,
                    ScheduledStartAtUtc = request.PreferredStartAtUtc,
                    ScheduledEndAtUtc = request.PreferredStartAtUtc.AddMinutes(DemoDurationMinutes),
                    Participants = [],
                },
                cancellationToken);

            return new StoreDemoBookingConfirmationDto
            {
                Id = booking.Id,
                ScheduledStartAtUtc = request.PreferredStartAtUtc,
                ScheduledEndAtUtc = request.PreferredStartAtUtc.AddMinutes(DemoDurationMinutes),
            };
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
