namespace iucs.readernest.application.Common.Interfaces
{
    /// <summary>
    /// Runs instant PromQL queries against the Prometheus HTTP API. Implemented in the API layer
    /// (needs HttpClient infrastructure the application layer deliberately doesn't reference).
    /// </summary>
    public interface IPrometheusClient
    {
        /// <summary>
        /// Returns the first result's value, or null on any failure (unreachable Prometheus,
        /// bad query, no matching series) — one missing metric must never take the whole
        /// dashboard call down with it.
        /// </summary>
        Task<double?> QueryScalarAsync(string baseUrl, string promql, CancellationToken cancellationToken = default);

        /// <summary>
        /// Same as <see cref="QueryScalarAsync"/> but keeps every result series (with its labels)
        /// instead of just the first — for queries like `rn_service_active{instance="X"}` where
        /// each service is its own labeled series and the caller needs all of them in one round trip.
        /// </summary>
        Task<IReadOnlyList<PrometheusSeries>> QueryVectorAsync(string baseUrl, string promql, CancellationToken cancellationToken = default);
    }

    public record PrometheusSeries(IReadOnlyDictionary<string, string> Labels, double Value);
}
