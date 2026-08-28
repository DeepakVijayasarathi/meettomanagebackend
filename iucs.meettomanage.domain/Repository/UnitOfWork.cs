using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Repository
{
    public class UnitOfWork : IUnitOfWork
    {
        /// <summary>PostgreSQL serialization_failure — SSI aborted us; the work is safe to redo.</summary>
        private const string SerializationFailureSqlState = "40001";

        /// <summary>PostgreSQL deadlock_detected — same story: the loser retries.</summary>
        private const string DeadlockDetectedSqlState = "40P01";

        /// <summary>
        /// Attempts, not retries. Serialization failures here mean two visitors picked the same
        /// slot at the same instant; a handful of attempts covers that comfortably, and giving up
        /// (rather than looping) keeps a genuinely contended slot from pinning a request open.
        /// </summary>
        private const int MaxAttempts = 4;

        private readonly MeetToManageDbContext _context;
        private readonly ConcurrentDictionary<Type, object> _repositories = new();

        public UnitOfWork(MeetToManageDbContext context)
        {
            _context = context;
        }

        public IRepository<TEntity> Repository<TEntity>() where TEntity : BaseEntity
        {
            return (IRepository<TEntity>)_repositories.GetOrAdd(
                typeof(TEntity),
                _ => new EfRepository<TEntity>(_context));
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return _context.SaveChangesAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<TResult> ExecuteInSerializableTransactionAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            // Nested call: the outermost transaction already owns isolation and retry, and
            // starting a second one here would either be ignored or throw depending on provider.
            if (_context.Database.CurrentTransaction is not null)
            {
                return await operation(cancellationToken);
            }

            for (var attempt = 1; ; attempt++)
            {
                if (attempt > 1)
                {
                    // The rolled-back attempt's inserts are gone from the database but its entities
                    // are still sitting in the tracker in the Added state; saving them again after
                    // the retry rebuilds its own would duplicate every row. Only done on a retry so
                    // the first, overwhelmingly common attempt never disturbs a caller's tracker.
                    _context.ChangeTracker.Clear();
                }

                var transaction = await _context.Database
                    .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                try
                {
                    var result = await operation(cancellationToken);

                    // PostgreSQL can also report the serialization failure here rather than at the
                    // statement that caused it, so COMMIT stays inside the guarded region.
                    await transaction.CommitAsync(cancellationToken);
                    return result;
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsSerializationFailure(ex))
                {
                    await transaction.RollbackAsync(CancellationToken.None);

                    // A brief, growing pause so two colliding requests don't just re-collide.
                    await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), cancellationToken);
                    continue;
                }
                finally
                {
                    await transaction.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// True when this exception (or anything it wraps — EF surfaces write failures as a
        /// DbUpdateException) is the database saying "I aborted you, run it again". Matched on the
        /// standard SQLSTATE via <see cref="DbException.SqlState"/> rather than a provider-specific
        /// exception type, so the domain layer stays free of an Npgsql dependency.
        /// </summary>
        private static bool IsSerializationFailure(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is DbException dbException
                    && dbException.SqlState is SerializationFailureSqlState or DeadlockDetectedSqlState)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
