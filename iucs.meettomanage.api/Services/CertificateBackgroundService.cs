using iucs.meettomanage.application.Services;

namespace iucs.meettomanage.api.Services
{
    /// <summary>Hourly: issues completion certificates for students in batches that have finished, and tells their families.</summary>
    public class CertificateBackgroundService(IServiceScopeFactory scopeFactory, ILogger<CertificateBackgroundService> logger) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var issued = await scope.ServiceProvider.GetRequiredService<ICertificateService>()
                        .IssueForCompletedBatchesAsync(notify: true, stoppingToken);
                    if (issued > 0) logger.LogInformation("Issued {Count} completion certificates.", issued);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Certificate issuing failed; retrying next interval.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
    }
}
