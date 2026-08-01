using System.Linq.Expressions;
using iucs.readernest.domain.Entities.Common;

namespace iucs.readernest.domain.Repository
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
    }
}
