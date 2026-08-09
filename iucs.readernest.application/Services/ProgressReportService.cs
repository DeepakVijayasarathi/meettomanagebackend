using iucs.readernest.application.Common.Exceptions;
using iucs.readernest.application.Dto.Communication;
using iucs.readernest.application.Mappings;
using iucs.readernest.domain.Entities.Communication;
using iucs.readernest.domain.Entities.Users;
using iucs.readernest.domain.Enums;
using iucs.readernest.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.readernest.application.Services
{
    public class ProgressReportService : IProgressReportService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;
        private readonly INotificationService _notificationService;

        public ProgressReportService(IUnitOfWork unitOfWork, IAuditLogService auditLog, INotificationService notificationService)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
            _notificationService = notificationService;
        }

        private IQueryable<ProgressReport> BaseQuery() =>
            _unitOfWork.Repository<ProgressReport>().Query()
                .Include(r => r.Child).ThenInclude(c => c.ParentProfile).ThenInclude(p => p.User);

        public async Task<IReadOnlyList<ProgressReportDto>> ListAsync(
            int? year,
            int? month,
            Guid? childId,
            CancellationToken cancellationToken = default)
        {
            var query = BaseQuery();
            if (year.HasValue)
            {
                query = query.Where(r => r.PeriodYear == year.Value);
            }
            if (month.HasValue)
            {
                query = query.Where(r => r.PeriodMonth == month.Value);
            }
            if (childId.HasValue)
            {
                query = query.Where(r => r.ChildId == childId.Value);
            }

            var reports = await query
                .OrderBy(r => r.Child.FirstName).ThenBy(r => r.Child.LastName)
                .ToListAsync(cancellationToken);
            return reports.Select(r => r.ToDto()).ToList();
        }

        public async Task<IReadOnlyList<ProgressReportDto>> ListForParentUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            // A parent only ever sees reports that have actually been sent — a draft is
            // internal working content until staff finish and send it.
            var reports = await BaseQuery()
                .Where(r => r.Child.ParentProfile.User.Id == userId && r.Status == ProgressReportStatus.Sent)
                .OrderByDescending(r => r.PeriodYear).ThenByDescending(r => r.PeriodMonth)
                .ToListAsync(cancellationToken);
            return reports.Select(r => r.ToDto()).ToList();
        }

        public async Task<ProgressReportDto> SaveContentAsync(
            Guid id,
            SaveProgressReportContentRequest request,
            CancellationToken cancellationToken = default)
        {
            // Load tracked (Query()/BaseQuery is AsNoTracking; mutating that never persists).
            var report = await _unitOfWork.Repository<ProgressReport>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(ProgressReport), id);

            if (report.Status != ProgressReportStatus.Draft)
            {
                throw new DomainValidationException("A sent report's content can no longer be edited.");
            }

            report.Content = request.Content;

            await _auditLog.StageAsync(AuditAction.Update, nameof(ProgressReport), report.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return (await BaseQuery().FirstAsync(r => r.Id == id, cancellationToken)).ToDto();
        }

        public async Task<ProgressReportDto> SendAsync(Guid id, CancellationToken cancellationToken = default)
        {
            // Load tracked (Query()/BaseQuery is AsNoTracking; mutating that never persists).
            var report = await _unitOfWork.Repository<ProgressReport>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(ProgressReport), id);

            if (report.Status != ProgressReportStatus.Draft)
            {
                throw new DomainValidationException($"A report in status '{report.Status}' cannot be sent again.");
            }

            if (string.IsNullOrWhiteSpace(report.Content))
            {
                throw new DomainValidationException("Write the report before sending it.");
            }

            var dto = (await BaseQuery().FirstAsync(r => r.Id == id, cancellationToken)).ToDto();

            report.Status = ProgressReportStatus.Sent;
            report.SentAtUtc = DateTime.UtcNow;

            await _auditLog.StageAsync(AuditAction.Update, nameof(ProgressReport), report.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var period = $"{dto.PeriodYear}-{dto.PeriodMonth:D2}";
            await _notificationService.SendTemplatedEmailAsync(
                dto.ParentUserId,
                dto.ParentEmail,
                NotificationType.PerformanceSummary,
                "progress-report",
                new Dictionary<string, string>
                {
                    ["ChildFirstName"] = dto.ChildName.Split(' ')[0],
                    ["Period"] = period,
                    ["Content"] = dto.Content,
                },
                cancellationToken);

            return (await BaseQuery().FirstAsync(r => r.Id == id, cancellationToken)).ToDto();
        }

        public async Task<int> EnsureMonthlyDraftsAsync(int year, int month, CancellationToken cancellationToken = default)
        {
            var activeChildIds = await _unitOfWork.Repository<Child>().Query()
                .Where(c => c.IsActive)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
            if (activeChildIds.Count == 0)
            {
                return 0;
            }

            var existingChildIds = await _unitOfWork.Repository<ProgressReport>().Query()
                .Where(r => r.PeriodYear == year && r.PeriodMonth == month && activeChildIds.Contains(r.ChildId))
                .Select(r => r.ChildId)
                .ToListAsync(cancellationToken);
            var existingSet = existingChildIds.ToHashSet();

            var created = 0;
            foreach (var childId in activeChildIds)
            {
                if (existingSet.Contains(childId))
                {
                    continue;
                }

                await _unitOfWork.Repository<ProgressReport>().AddAsync(new ProgressReport
                {
                    ChildId = childId,
                    PeriodYear = year,
                    PeriodMonth = month,
                }, cancellationToken);
                created++;
            }

            if (created > 0)
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return created;
        }
    }
}
