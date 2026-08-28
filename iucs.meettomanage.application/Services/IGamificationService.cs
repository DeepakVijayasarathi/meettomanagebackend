using iucs.meettomanage.application.Dto.Sessions;

namespace iucs.meettomanage.application.Services
{
    public interface IGamificationService
    {
        /// <summary>
        /// Persists an award; star grants auto-create milestone awards when the
        /// participant's session stars cross 3/6/10. callerUserId gates it: a session-scoped
        /// award requires genuine participation in that session (Admin/assigned teacher/
        /// enrolled parent), a Badge requires Teacher/Admin, and Milestone can never be
        /// requested directly — it's server-computed only.
        /// </summary>
        Task<IReadOnlyList<AwardDto>> GrantAsync(Guid callerUserId, GrantAwardRequest request, CancellationToken cancellationToken = default);

        /// <summary>Aggregated stars + badges per participant — session-scoped or all-time.</summary>
        Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(Guid? sessionId, int top, CancellationToken cancellationToken = default);
    }
}
