using iucs.meettomanage.application.Dto.Monitoring;

namespace iucs.meettomanage.application.Services
{
    public interface IRecordingPipelineService
    {
        /// <summary>Null if main could not be reached -- callers should treat that as "unknown".</summary>
        Task<RecordingPipelineDto?> GetAsync(CancellationToken cancellationToken = default);
    }
}
