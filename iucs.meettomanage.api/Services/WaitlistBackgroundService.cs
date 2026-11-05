using iucs.meettomanage.application.Services;

namespace iucs.meettomanage.api.Services
{
    /// <summary>Hourly: offers seats that opened in full batches to the next students on the waitlist.</summary>
    public class WaitlistBackgroundService(IServiceScopeFactory scopeFactory, ILogger<WaitlistBackgroundService> logger) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var offered = await scope.ServiceProvider.GetRequiredService<IWaitlistService>()
                        .OfferOpenSeatsAsync(stoppingToken);
                    if (offered > 0) logger.LogInformation("Offered {Count} waitlist seats.", offered);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Waitlist offers failed; retrying next interval.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
    }
}
