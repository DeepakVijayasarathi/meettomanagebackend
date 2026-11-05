using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Dto.Sessions;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Sessions;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.application.Services
{
    public class GamificationService : IGamificationService
    {
        // Session-star thresholds that auto-grant a milestone (mirrors the live overlay)
        private static readonly (int Stars, string Label)[] Milestones =
        [
            (3, "Rising Star — 3 stars"),
            (6, "Star Reader — 6 stars"),
            (10, "Champion — 10 stars"),
        ];

        private readonly IUnitOfWork _unitOfWork;
        private readonly ISessionService _sessionService;

        public GamificationService(IUnitOfWork unitOfWork, ISessionService sessionService)
        {
            _unitOfWork = unitOfWork;
            _sessionService = sessionService;
        }

        public async Task<IReadOnlyList<AwardDto>> GrantAsync(
            Guid callerUserId, GrantAwardRequest request, CancellationToken cancellationToken = default)
        {
            // Milestones are exclusively server-computed below when stars cross a
            // threshold — a client must never be able to mint one directly.
            if (request.Kind == AwardKind.Milestone)
            {
                throw new DomainValidationException("Milestone awards are granted automatically and cannot be requested directly.");
            }

            var caller = await _unitOfWork.Repository<User>().GetByIdAsync(callerUserId, cancellationToken)
                ?? throw new UnauthorizedException("Unknown user.");
            var isStaff = caller.Role is UserRole.Admin or UserRole.Teacher;

            // A named Badge is "granted by a teacher or an activity" (see AwardKind) — never
            // self-awarded by a student/parent. A Star is the live-quiz self-report flow any
            // genuine session participant can post for themselves.
            if (request.Kind == AwardKind.Badge && !isStaff)
            {
                throw new ForbiddenException("Only a teacher or admin can grant a badge.");
            }

            if (request.SessionId.HasValue)
            {
                if (!await _sessionService.IsSessionParticipantAsync(request.SessionId.Value, callerUserId, cancellationToken))
                {
                    throw new ForbiddenException("You do not have access to this session.");
                }
            }
            else if (!isStaff)
            {
                // No session to scope against — only a trusted role can award outside a
                // specific, ownership-checked class.
                throw new ForbiddenException("An award not tied to a specific session can only be granted by a teacher or admin.");
            }

            var name = request.ParticipantName.Trim();

            // The self-report Star flow (a parent's child answering their own live quiz) took
            // ParticipantName as free text with no check it was actually the caller's own
            // child — any session participant could post a Star under a classmate's name (or
            // any name at all), inflating that name's persisted award history and leaderboard
            // rank. Scoped to Parent + Star + session-scoped specifically: that's the only
            // combination reachable through the self-report path (staff-granted badges/stars
            // legitimately name a student who isn't the caller, and IsSessionParticipantAsync's
            // own Parent branch already guarantees session.BatchId is set for this caller to
            // have gotten this far, so there's always a batch to check enrollment against).
            if (caller.Role == UserRole.Parent && request.Kind == AwardKind.Star && request.SessionId.HasValue)
            {
                var session = await _unitOfWork.Repository<ClassSession>().GetByIdAsync(request.SessionId.Value, cancellationToken);
                var ownChildNames = session?.BatchId is Guid batchId
                    ? await _unitOfWork.Repository<Child>().Query()
                        .Where(c => c.ParentProfile.UserId == callerUserId)
                        .Join(
                            _unitOfWork.Repository<BatchEnrollment>().Query().Where(e => e.BatchId == batchId),
                            c => c.Id,
                            e => e.ChildId,
                            (c, _) => c.FirstName + " " + c.LastName)
                        .ToListAsync(cancellationToken)
                    : [];

                if (!ownChildNames.Any(n => n.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ForbiddenException("You can only report a star for your own child in this class.");
                }
            }
            var repository = _unitOfWork.Repository<StudentAward>();
            var granted = new List<StudentAward>();

            var award = new StudentAward
            {
                ClassSessionId = request.SessionId,
                ParticipantName = name,
                Kind = request.Kind,
                Label = request.Label,
                Points = request.Points,
            };
            await repository.AddAsync(award, cancellationToken);
            granted.Add(award);

            if (request.Kind == AwardKind.Star && request.SessionId.HasValue)
            {
                var previousStars = await repository.Query()
                    .Where(a => a.ClassSessionId == request.SessionId
                        && a.ParticipantName == name
                        && a.Kind == AwardKind.Star)
                    .SumAsync(a => a.Points, cancellationToken);
                var totalStars = previousStars + request.Points;

                // Grant every milestone the new total crosses (a multi-point award can cross more than one)
                foreach (var (stars, label) in Milestones)
                {
                    if (previousStars < stars && totalStars >= stars)
                    {
                        var milestone = new StudentAward
                        {
                            ClassSessionId = request.SessionId,
                            ParticipantName = name,
                            Kind = AwardKind.Milestone,
                            Label = label,
                            Points = 0,
                        };
                        await repository.AddAsync(milestone, cancellationToken);
                        granted.Add(milestone);
                    }
                }
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return granted.Select(ToDto).ToList();
        }

        public async Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(
            Guid? sessionId,
            int top,
            CancellationToken cancellationToken = default)
        {
            var query = _unitOfWork.Repository<StudentAward>().Query();
            if (sessionId.HasValue)
            {
                query = query.Where(a => a.ClassSessionId == sessionId.Value);
            }

            var awards = await query.ToListAsync(cancellationToken);
            return awards
                .GroupBy(a => a.ParticipantName)
                .Select(g => new LeaderboardEntryDto
                {
                    ParticipantName = g.Key,
                    Stars = g.Where(a => a.Kind == AwardKind.Star).Sum(a => a.Points),
                    Badges = g.Where(a => a.Kind != AwardKind.Star && a.Label != null)
                        .OrderBy(a => a.CreatedAtUtc)
                        .Select(a => a.Label!)
                        .Distinct()
                        .ToList(),
                })
                .OrderByDescending(e => e.Stars)
                .Take(Math.Clamp(top, 1, 100))
                .ToList();
        }

        public async Task<ChildRewardsDto> GetChildRewardsAsync(
            Guid parentUserId, Guid childId, CancellationToken cancellationToken = default)
        {
            var parent = await _unitOfWork.Repository<ParentProfile>().Query()
                .FirstOrDefaultAsync(p => p.UserId == parentUserId, cancellationToken)
                ?? throw new NotFoundException("No parent profile is linked to the current account.");

            // Another family's child looks exactly like a missing one — never confirm it exists.
            var child = await _unitOfWork.Repository<Child>().Query().AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == childId && c.ParentProfileId == parent.Id, cancellationToken)
                ?? throw new NotFoundException(nameof(Child), childId);

            // Awards are persisted under the participant's full name (ChildId is rarely set), the same
            // "First Last" the live-class self-report check compares against.
            var name = $"{child.FirstName} {child.LastName}".Trim();
            var awards = (await _unitOfWork.Repository<StudentAward>().Query().AsNoTracking()
                    .Where(a => a.ChildId == childId || (a.ChildId == null && a.ParticipantName.ToLower() == name.ToLower()))
                    .ToListAsync(cancellationToken))
                .ToList();

            var stars = awards.Where(a => a.Kind == AwardKind.Star).ToList();
            var totalStars = stars.Sum(a => a.Points);
            var since = DateTime.UtcNow.AddDays(-30);

            // The same badge earned in several classes is one reward, shown at its first earning.
            var rewards = awards
                .Where(a => a.Kind != AwardKind.Star && !string.IsNullOrWhiteSpace(a.Label))
                .GroupBy(a => a.Label!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(a => a.CreatedAtUtc).First())
                .OrderByDescending(a => a.CreatedAtUtc)
                .Select(a => new ChildRewardDto { Label = a.Label!.Trim(), Kind = a.Kind, EarnedAtUtc = a.CreatedAtUtc })
                .ToList();

            // Milestones are earned per class (3/6/10 stars in one session); the next target here is
            // therefore against the best single class so far, which is what the thresholds measure.
            var bestInOneClass = stars.Where(a => a.ClassSessionId != null)
                .GroupBy(a => a.ClassSessionId)
                .Select(g => g.Sum(a => a.Points))
                .DefaultIfEmpty(0)
                .Max();
            var next = Milestones.Cast<(int Stars, string Label)?>().FirstOrDefault(m => m!.Value.Stars > bestInOneClass);

            return new ChildRewardsDto
            {
                ChildId = child.Id,
                ChildName = name,
                TotalStars = totalStars,
                StarsLast30Days = stars.Where(a => a.CreatedAtUtc >= since).Sum(a => a.Points),
                Rewards = rewards,
                NextMilestoneStars = next?.Stars,
                NextMilestoneLabel = next?.Label,
            };
        }

        private static AwardDto ToDto(StudentAward award) => new()
        {
            Id = award.Id,
            SessionId = award.ClassSessionId,
            ParticipantName = award.ParticipantName,
            Kind = award.Kind,
            Label = award.Label,
            Points = award.Points,
            CreatedAtUtc = award.CreatedAtUtc,
        };
    }
}
