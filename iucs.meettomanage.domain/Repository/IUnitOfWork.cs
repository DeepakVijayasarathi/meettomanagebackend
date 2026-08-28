using iucs.meettomanage.domain.Entities.Common;

namespace iucs.meettomanage.domain.Repository
{
    /// <summary>
    /// Groups repository work into a single transaction boundary; services mutate
    /// through repositories and persist once via <see cref="SaveChangesAsync"/>.
    /// </summary>
    public interface IUnitOfWork
    {
        IRepository<TEntity> Repository<TEntity>() where TEntity : BaseEntity;

        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs <paramref name="operation"/> inside a SERIALIZABLE transaction, retrying it from
        /// scratch if the database aborts it with a serialization failure, and commits once it
        /// returns.
        /// <para>
        /// Use this for a check-then-insert that a row lock cannot protect because there is no
        /// single row to lock — "nothing overlaps this teacher's requested time range, so insert
        /// a session into it". Under READ COMMITTED two such transactions can both read "free"
        /// and both insert; under SERIALIZABLE, PostgreSQL's SSI tracks the read's predicate and
        /// the conflicting insert, detects the resulting dependency cycle and aborts one side with
        /// SQLSTATE 40001, which this method catches and retries against the now-current state.
        /// The reads that justify the write MUST happen inside <paramref name="operation"/> —
        /// reading outside it and only writing inside defeats the whole mechanism.
        /// </para>
        /// <para>
        /// Because a retry re-executes <paramref name="operation"/> from the top, the delegate must
        /// contain database work only: no emails, no gateway calls, no CRM pushes, nothing that
        /// cannot be undone by a rollback. Do those after this method returns. Entities staged by
        /// a failed attempt are discarded (the change tracker is cleared) before each retry, so the
        /// delegate must build the entities it saves rather than closing over ones created earlier.
        /// If a transaction is already open on this unit of work the delegate simply joins it — the
        /// outermost caller owns isolation and retry.
        /// </para>
        /// <para>
        /// The guarantee only holds against other SERIALIZABLE transactions: SSI cannot see a
        /// concurrent READ COMMITTED writer, so a conflicting row committed by a code path that
        /// does not use this method is not detected.
        /// </para>
        /// </summary>
        Task<TResult> ExecuteInSerializableTransactionAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default);
    }
}
