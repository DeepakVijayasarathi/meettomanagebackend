using iucs.readernest.application.Common.Exceptions;
using iucs.readernest.application.Dto.Courses;
using iucs.readernest.application.Mappings;
using iucs.readernest.domain.Entities.Academics;
using iucs.readernest.domain.Entities.Billing;
using iucs.readernest.domain.Entities.Sessions;
using iucs.readernest.domain.Enums;
using iucs.readernest.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.readernest.application.Services
{
    public class CourseService : ICourseService
    {
        private static readonly int[] AllowedDurations = [30, 45, 60];

        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;

        public CourseService(IUnitOfWork unitOfWork, IAuditLogService auditLog)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
        }

        public async Task<IReadOnlyList<CourseCategoryDto>> ListCategoriesAsync(CancellationToken cancellationToken = default)
        {
            var categories = await _unitOfWork.Repository<CourseCategory>().Query()
                .OrderBy(c => c.Name)
                .ToListAsync(cancellationToken);

            return categories.Select(c => c.ToDto()).ToList();
        }

        public async Task<CourseCategoryDto> CreateCategoryAsync(
            CreateCourseCategoryRequest request,
            CancellationToken cancellationToken = default)
        {
            var name = request.Name.Trim();
            var repository = _unitOfWork.Repository<CourseCategory>();

            if (await repository.ExistsAsync(c => c.Name == name, cancellationToken))
            {
                throw new ConflictException($"A course category named '{name}' already exists.");
            }

            var category = new CourseCategory
            {
                Name = name,
                Description = request.Description,
                Department = request.Department,
            };
            await repository.AddAsync(category, cancellationToken);
            await _auditLog.StageAsync(AuditAction.Create, nameof(CourseCategory), category.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return category.ToDto();
        }

        public async Task<IReadOnlyList<CourseDto>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
        {
            var query = _unitOfWork.Repository<Course>().Query().Include(c => c.CourseCategory).AsQueryable();
            if (!includeInactive)
            {
                query = query.Where(c => c.IsActive);
            }

            var courses = await query.OrderBy(c => c.Name).ToListAsync(cancellationToken);
            var dtos = courses.Select(c => c.ToDto()).ToList();
            await ApplyStatsAsync(dtos, cancellationToken);
            return dtos;
        }

        /// <summary>
        /// Fills ActiveBatches/TotalEnrolled/Revenue — deliberately not part of Course.ToDto()
        /// since they're cross-table aggregates (Batch/BatchEnrollment/Invoice), not fields on
        /// the Course entity itself. Revenue is AmountPaid summed over invoices tied to the
        /// course (Invoice.CourseId), so it reflects money actually collected, not billed.
        /// </summary>
        private async Task ApplyStatsAsync(IReadOnlyList<CourseDto> dtos, CancellationToken cancellationToken)
        {
            if (dtos.Count == 0) return;

            var courseIds = dtos.Select(d => d.Id).ToList();

            var activeBatchCounts = await _unitOfWork.Repository<Batch>().Query()
                .Where(b => courseIds.Contains(b.CourseId) && b.Status == BatchStatus.Active)
                .GroupBy(b => b.CourseId)
                .Select(g => new { CourseId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.CourseId, x => x.Count, cancellationToken);

            var enrolledCounts = await _unitOfWork.Repository<BatchEnrollment>().Query()
                .Where(e => e.Status == EnrollmentStatus.Active && courseIds.Contains(e.Batch.CourseId))
                .GroupBy(e => e.Batch.CourseId)
                .Select(g => new { CourseId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.CourseId, x => x.Count, cancellationToken);

            var revenueByCourse = await _unitOfWork.Repository<Invoice>().Query()
                .Where(i => i.CourseId != null && courseIds.Contains(i.CourseId.Value))
                .GroupBy(i => i.CourseId!.Value)
                .Select(g => new { CourseId = g.Key, Amount = g.Sum(i => i.AmountPaid) })
                .ToDictionaryAsync(x => x.CourseId, x => x.Amount, cancellationToken);

            foreach (var dto in dtos)
            {
                dto.ActiveBatches = activeBatchCounts.GetValueOrDefault(dto.Id);
                dto.TotalEnrolled = enrolledCounts.GetValueOrDefault(dto.Id);
                dto.Revenue = revenueByCourse.GetValueOrDefault(dto.Id);
            }
        }

        public async Task<IReadOnlyList<CourseOptionDto>> ListOptionsAsync(CancellationToken cancellationToken = default)
        {
            return await _unitOfWork.Repository<Course>().Query()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name)
                .Select(c => new CourseOptionDto { Id = c.Id, Name = c.Name })
                .ToListAsync(cancellationToken);
        }

        public async Task<CourseDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var course = await _unitOfWork.Repository<Course>().Query()
                .Include(c => c.CourseCategory)
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(Course), id);

            var dto = course.ToDto();
            await ApplyStatsAsync([dto], cancellationToken);
            return dto;
        }

        public async Task<CourseDto> CreateAsync(SaveCourseRequest request, CancellationToken cancellationToken = default)
        {
            var category = await ValidateAsync(request, cancellationToken);

            var course = new Course
            {
                CourseCategoryId = request.CourseCategoryId,
                CourseCategory = category,
                Name = request.Name.Trim(),
                Description = request.Description,
                Type = request.Type,
                DurationMinutes = request.DurationMinutes,
                Price = request.Price,
                TotalSessions = request.TotalSessions,
                Department = request.Department,
                IsActive = request.IsActive,
            };
            await _unitOfWork.Repository<Course>().AddAsync(course, cancellationToken);
            await _auditLog.StageAsync(AuditAction.Create, nameof(Course), course.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return course.ToDto();
        }

        public async Task<CourseDto> UpdateAsync(Guid id, SaveCourseRequest request, CancellationToken cancellationToken = default)
        {
            var category = await ValidateAsync(request, cancellationToken);
            var course = await _unitOfWork.Repository<Course>().GetByIdAsync(id, cancellationToken)
                ?? throw new NotFoundException(nameof(Course), id);

            // Switching to Individual bypasses BatchService's own "one student per
            // Individual batch" guard entirely, since that guard only fires when a BATCH
            // is edited — not when the course backing it changes type out from under it.
            if (request.Type == CourseType.Individual && course.Type != CourseType.Individual)
            {
                var hasMultiStudentBatch = await _unitOfWork.Repository<BatchEnrollment>().Query()
                    .Where(e => e.Status == EnrollmentStatus.Active && e.Batch.CourseId == id)
                    .GroupBy(e => e.BatchId)
                    .AnyAsync(g => g.Count() > 1, cancellationToken);
                if (hasMultiStudentBatch)
                {
                    throw new DomainValidationException(
                        "This course has a batch with more than one active student; it can't switch to Individual " +
                        "until students are moved to separate batches.");
                }
            }

            // TotalSessions/DurationMinutes are baked into any schedule already generated
            // from them (SessionService.GenerateScheduleAsync). Changing either afterward
            // desyncs MoveBatchToDormantIfCourseCompletedAsync's completedSessions vs.
            // TotalSessions check against a schedule sized for the old values.
            if ((request.TotalSessions != course.TotalSessions || request.DurationMinutes != course.DurationMinutes))
            {
                var batchIds = await _unitOfWork.Repository<Batch>().Query()
                    .Where(b => b.CourseId == id)
                    .Select(b => b.Id)
                    .ToListAsync(cancellationToken);
                var hasGeneratedSchedule = batchIds.Count > 0 && await _unitOfWork.Repository<ClassSession>()
                    .ExistsAsync(s => s.BatchId != null && batchIds.Contains(s.BatchId.Value), cancellationToken);
                if (hasGeneratedSchedule)
                {
                    throw new DomainValidationException(
                        "This course already has a batch with a generated schedule; TotalSessions and " +
                        "DurationMinutes can no longer change without desyncing it.");
                }
            }

            course.CourseCategoryId = request.CourseCategoryId;
            course.CourseCategory = category;
            course.Name = request.Name.Trim();
            course.Description = request.Description;
            course.Type = request.Type;
            course.DurationMinutes = request.DurationMinutes;
            course.Price = request.Price;
            course.TotalSessions = request.TotalSessions;
            course.Department = request.Department;
            course.IsActive = request.IsActive;

            await _auditLog.StageAsync(AuditAction.Update, nameof(Course), course.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return course.ToDto();
        }

        private async Task<CourseCategory> ValidateAsync(SaveCourseRequest request, CancellationToken cancellationToken)
        {
            if (!AllowedDurations.Contains(request.DurationMinutes))
            {
                throw new DomainValidationException("Class duration must be 30, 45 or 60 minutes.");
            }

            return await _unitOfWork.Repository<CourseCategory>().GetByIdAsync(request.CourseCategoryId, cancellationToken)
                ?? throw new NotFoundException(nameof(CourseCategory), request.CourseCategoryId);
        }
    }
}
