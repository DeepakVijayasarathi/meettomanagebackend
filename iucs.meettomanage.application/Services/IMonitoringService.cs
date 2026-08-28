using iucs.meettomanage.application.Dto.Monitoring;

namespace iucs.meettomanage.application.Services
{
    public interface IMonitoringService
    {
        Task<MonitoringSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);
    }
}
