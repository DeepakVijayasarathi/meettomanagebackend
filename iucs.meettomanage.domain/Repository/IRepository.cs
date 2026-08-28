using System.Linq.Expressions;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore.Query;

namespace iucs.meettomanage.domain.Repository
{
    /// <summary>
    /// Generic data access for any <see cref="BaseEntity"/>-derived aggregate.
    /// Removal is always a soft delete (converted by the audit interceptor).
    /// </summary>
    public interface IRepository<TEntity> where TEntity : BaseEntity
    {
        Task<TEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>Tracked lookup by predicate, for read-then-mutate flows.</summary>
        Task<TEntity?> FirstOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken cancellationToken = default);

        Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);

        Task<bool> ExistsAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);

        Task AddAsync(TEntity entity, CancellationToken cancellationToken = default);

        void Update(TEntity entity);

        void Remove(TEntity entity);

        /// <summary>Composable no-tracking query for service-layer filtering, paging and projections.</summary>
        IQueryable<TEntity> Query();

        /// <summary>
        /// Composable TRACKED query for bulk read-then-mutate flows — filter down to the rows
        /// that need updating, mutate each in memory, and a single SaveChangesAsync persists
        /// all of them. Use this instead of the ids-then-loop-GetByIdAsync pattern (one query
        /// to find candidates, then N more round trips to re-fetch each one tracked) that
        /// otherwise shows up anywhere a bulk status flip is needed.
        /// </summary>
        IQueryable<TEntity> TrackedQuery();

        /// <summary>
        /// Single-statement conditional UPDATE — <c>UPDATE ... SET ... WHERE &lt;predicate&gt;</c> —
        /// issued straight to the database, returning the number of rows it actually matched.
        /// <para>
        /// This is the atomic building block for a check-then-mutate state transition that must
        /// not be won twice (e.g. "move this refund out of Requested, but only if it is still
        /// Requested"). Because the check lives in the UPDATE's own WHERE clause, the database —
        /// not the application — arbitrates: under READ COMMITTED, a second concurrent UPDATE of
        /// the same row blocks on that row's write lock until the first commits, then re-evaluates
        /// its WHERE clause against the now-committed row and matches 0 rows. A 0 return therefore
        /// means "someone else got there first", and the caller must treat it as a conflict rather
        /// than carrying on.
        /// </para>
        /// <para>
        /// Two caveats, both consequences of bypassing the change tracker:
        /// (1) the audit interceptor only runs inside SaveChanges, so it does NOT fire here —
        /// callers must set <c>UpdatedAtUtc</c> (and <c>UpdatedBy</c> on an AuditEntity) in the
        /// setters by hand; (2) entities already tracked by this context are not refreshed, so
        /// don't mix this with an in-memory mutation of the same property in the same unit of work.
        /// The global soft-delete filter still applies, so deleted rows are never matched.
        /// </para>
        /// </summary>
        Task<int> ExecuteUpdateAsync(
            Expression<Func<TEntity, bool>> predicate,
            Expression<Func<SetPropertyCalls<TEntity>, SetPropertyCalls<TEntity>>> setters,
            CancellationToken cancellationToken = default);
    }
}
