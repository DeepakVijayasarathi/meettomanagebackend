using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Learning;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.application.Services
{
    public class MitoInsightsDto
    {
        public int WindowDays { get; set; }
        public int DailyLimit { get; set; }
        public int TotalRequests { get; set; }
        public int ActiveStudents { get; set; }
        public int StudentsAtLimitToday { get; set; }
        public int OpenFlags { get; set; }
        public IReadOnlyList<MitoUsagePoint> ByDay { get; set; } = [];
        public IReadOnlyDictionary<string, int> ByMode { get; set; } = new Dictionary<string, int>();
        public IReadOnlyList<MitoStudentUsage> TopStudents { get; set; } = [];
    }

    public record MitoUsagePoint(DateOnly Date, int Requests, int Students);

    public record MitoStudentUsage(Guid ChildId, string Name, int Requests, DateOnly LastUsed);

    public class MitoFlagDto
    {
        public Guid Id { get; set; }
        public Guid ChildId { get; set; }
        public string ChildName { get; set; } = null!;
        public string Mode { get; set; } = null!;
        public string Question { get; set; } = null!;
        public string Answer { get; set; } = null!;
        public string? Reason { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ResolvedAtUtc { get; set; }
        public string? Resolution { get; set; }
    }

    public class FlagMitoAnswerRequest
    {
        [Required] public Guid ChildId { get; set; }
        [RegularExpression("doubt|plan|review|general")] public string Mode { get; set; } = "general";
        [Required, StringLength(2000, MinimumLength = 1)] public string Question { get; set; } = null!;
        [Required, StringLength(4000, MinimumLength = 1)] public string Answer { get; set; } = null!;
        [StringLength(500)] public string? Reason { get; set; }
    }

    public class ResolveMitoFlagRequest
    {
        [StringLength(2000)] public string? Resolution { get; set; }
    }

    public interface IMitoUsageService
    {
        /// <summary>Counts one request for the student and refuses it once the academy's daily limit is used up. Returns how many are left today (null when unlimited).</summary>
        Task<int?> RecordAsync(Guid childId, string mode, int dailyLimit, CancellationToken cancellationToken = default);
        Task<MitoInsightsDto> GetInsightsAsync(int days, int dailyLimit, Guid? teacherUserId, CancellationToken cancellationToken = default);
        Task FlagAsync(FlagMitoAnswerRequest request, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<MitoFlagDto>> ListFlagsAsync(bool open, Guid? teacherUserId, CancellationToken cancellationToken = default);
        Task<MitoFlagDto> ResolveFlagAsync(Guid flagId, Guid actorUserId, Guid? teacherUserId, string? resolution, CancellationToken cancellationToken = default);
    }

    /// <summary>Usage limits, the admin usage dashboard and teacher review of flagged answers for the Mito companion.</summary>
    public class MitoUsageService(IUnitOfWork unitOfWork) : IMitoUsageService
    {
        public const int DefaultDailyLimit = 40;

        public async Task<int?> RecordAsync(Guid childId, string mode, int dailyLimit, CancellationToken cancellationToken = default)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var repository = unitOfWork.Repository<MitoUsageDay>();
            var row = await repository.TrackedQuery().FirstOrDefaultAsync(x => x.ChildId == childId && x.Date == today, cancellationToken);
            if (row is null)
            {
                row = new MitoUsageDay { ChildId = childId, Date = today };
                await repository.AddAsync(row, cancellationToken);
            }
            if (dailyLimit > 0 && row.Requests >= dailyLimit)
                throw new DomainValidationException($"That's all of today's {dailyLimit} Mito questions. Come back tomorrow, or ask your teacher in class.");

            row.Requests++;
            switch (mode)
            {
                case "doubt": row.DoubtRequests++; break;
                case "plan": row.PlanRequests++; break;
                case "review": row.ReviewRequests++; break;
                case "quiz": row.QuizRequests++; break;
            }
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return dailyLimit > 0 ? Math.Max(0, dailyLimit - row.Requests) : null;
        }

        public async Task<MitoInsightsDto> GetInsightsAsync(int days, int dailyLimit, Guid? teacherUserId, CancellationToken cancellationToken = default)
        {
            days = Math.Clamp(days, 7, 90);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var since = today.AddDays(-(days - 1));
            var scope = await ScopeAsync(teacherUserId, cancellationToken);
            var query = unitOfWork.Repository<MitoUsageDay>().Query().Where(x => x.Date >= since);
            if (scope is not null) query = query.Where(x => scope.Contains(x.ChildId));
            var rows = await query.Select(x => new
            {
                x.ChildId, x.Date, x.Requests, x.DoubtRequests, x.PlanRequests, x.ReviewRequests, x.QuizRequests,
                Name = x.Child.FirstName + " " + x.Child.LastName,
            }).ToListAsync(cancellationToken);

            var flags = unitOfWork.Repository<MitoAnswerFlag>().Query().Where(x => x.ResolvedAtUtc == null);
            if (scope is not null) flags = flags.Where(x => scope.Contains(x.ChildId));

            var byDate = rows.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => (Requests: g.Sum(x => x.Requests), Students: g.Count()));
            var general = rows.Sum(x => x.Requests - x.DoubtRequests - x.PlanRequests - x.ReviewRequests - x.QuizRequests);
            return new MitoInsightsDto
            {
                WindowDays = days,
                DailyLimit = dailyLimit,
                TotalRequests = rows.Sum(x => x.Requests),
                ActiveStudents = rows.Select(x => x.ChildId).Distinct().Count(),
                StudentsAtLimitToday = dailyLimit > 0 ? rows.Count(x => x.Date == today && x.Requests >= dailyLimit) : 0,
                OpenFlags = await flags.CountAsync(cancellationToken),
                ByDay = Enumerable.Range(0, days).Select(i => since.AddDays(i))
                    .Select(d => byDate.TryGetValue(d, out var v) ? new MitoUsagePoint(d, v.Requests, v.Students) : new MitoUsagePoint(d, 0, 0)).ToList(),
                ByMode = new Dictionary<string, int>
                {
                    ["doubt"] = rows.Sum(x => x.DoubtRequests),
                    ["plan"] = rows.Sum(x => x.PlanRequests),
                    ["review"] = rows.Sum(x => x.ReviewRequests),
                    ["quiz"] = rows.Sum(x => x.QuizRequests),
                    ["general"] = Math.Max(0, general),
                },
                TopStudents = rows.GroupBy(x => x.ChildId)
                    .Select(g => new MitoStudentUsage(g.Key, g.First().Name.Trim(), g.Sum(x => x.Requests), g.Max(x => x.Date)))
                    .OrderByDescending(x => x.Requests).Take(10).ToList(),
            };
        }

        public async Task FlagAsync(FlagMitoAnswerRequest request, CancellationToken cancellationToken = default)
        {
            await unitOfWork.Repository<MitoAnswerFlag>().AddAsync(new MitoAnswerFlag
            {
                ChildId = request.ChildId,
                Mode = request.Mode,
                Question = request.Question.Trim(),
                Answer = request.Answer.Trim(),
                Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            }, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<MitoFlagDto>> ListFlagsAsync(bool open, Guid? teacherUserId, CancellationToken cancellationToken = default)
        {
            var scope = await ScopeAsync(teacherUserId, cancellationToken);
            var query = unitOfWork.Repository<MitoAnswerFlag>().Query().Where(x => open ? x.ResolvedAtUtc == null : x.ResolvedAtUtc != null);
            if (scope is not null) query = query.Where(x => scope.Contains(x.ChildId));
            return await query.OrderByDescending(x => x.CreatedAtUtc).Take(200).Select(x => new MitoFlagDto
            {
                Id = x.Id, ChildId = x.ChildId, ChildName = x.Child.FirstName + " " + x.Child.LastName, Mode = x.Mode,
                Question = x.Question, Answer = x.Answer, Reason = x.Reason, CreatedAtUtc = x.CreatedAtUtc,
                ResolvedAtUtc = x.ResolvedAtUtc, Resolution = x.Resolution,
            }).ToListAsync(cancellationToken);
        }

        public async Task<MitoFlagDto> ResolveFlagAsync(Guid flagId, Guid actorUserId, Guid? teacherUserId, string? resolution, CancellationToken cancellationToken = default)
        {
            var scope = await ScopeAsync(teacherUserId, cancellationToken);
            var flag = await unitOfWork.Repository<MitoAnswerFlag>().TrackedQuery().Include(x => x.Child)
                .FirstOrDefaultAsync(x => x.Id == flagId, cancellationToken);
            if (flag is null || (scope is not null && !scope.Contains(flag.ChildId))) throw new NotFoundException("Flagged answer not found.");
            flag.ResolvedAtUtc = DateTime.UtcNow;
            flag.ResolvedByUserId = actorUserId;
            flag.Resolution = string.IsNullOrWhiteSpace(resolution) ? null : resolution.Trim();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new MitoFlagDto
            {
                Id = flag.Id, ChildId = flag.ChildId, ChildName = $"{flag.Child.FirstName} {flag.Child.LastName}", Mode = flag.Mode,
                Question = flag.Question, Answer = flag.Answer, Reason = flag.Reason, CreatedAtUtc = flag.CreatedAtUtc,
                ResolvedAtUtc = flag.ResolvedAtUtc, Resolution = flag.Resolution,
            };
        }

        /// <summary>Teachers see only students in their own active batches; staff (null) see everyone.</summary>
        private async Task<List<Guid>?> ScopeAsync(Guid? teacherUserId, CancellationToken cancellationToken)
        {
            if (teacherUserId is null) return null;
            var teacherId = await unitOfWork.Repository<TeacherProfile>().Query()
                .Where(t => t.UserId == teacherUserId).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(cancellationToken)
                ?? throw new ForbiddenException("Only teachers and staff can review Mito.");
            return await unitOfWork.Repository<BatchEnrollment>().Query()
                .Where(e => e.Batch.TeacherProfileId == teacherId && e.Status == EnrollmentStatus.Active)
                .Select(e => e.ChildId).Distinct().ToListAsync(cancellationToken);
        }
    }
}
