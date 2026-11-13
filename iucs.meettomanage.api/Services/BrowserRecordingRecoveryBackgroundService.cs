using iucs.meettomanage.application.Services;

namespace iucs.meettomanage.api.Services
{
    /// <summary>
    /// Safety net for recordings made in a teacher's browser: if the browser crashed, the laptop
    /// died or the window was closed before the recording was saved, the parts already uploaded
    /// are joined into the recording here, without anyone having to come back. An upload counts
    /// as stalled once no part has been asked for in 20 minutes (a recording asks every minute).
    /// </summary>
    public class BrowserRecordingRecoveryBackgroundService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(20);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BrowserRecordingRecoveryBackgroundService> _logger;

        public BrowserRecordingRecoveryBackgroundService(IServiceScopeFactory scopeFactory, ILogger<BrowserRecordingRecoveryBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
                    var saved = await sessions.RecoverStalledBrowserRecordingsAsync(StalledAfter, stoppingToken);
                    if (saved > 0)
                    {
                        _logger.LogInformation("Recovered {Count} interrupted browser recording(s).", saved);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Browser recording recovery failed; retrying next interval.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
    }
}
