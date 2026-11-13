using iucs.meettomanage.api.Saas;
using iucs.meettomanage.application.Services;

namespace iucs.meettomanage.api.Services
{
    /// <summary>
    /// Safety net for recordings made in a teacher's browser: if the browser crashed, the laptop
    /// died or the window was closed before the recording was saved, the parts already uploaded
    /// are joined into the recording here, without anyone having to come back. An upload counts
    /// as stalled once no part has been asked for in 20 minutes (a recording checks in at least
    /// every 5). Each upload is handled in its own scope, so one failure can't hold up the rest;
    /// one the plan has no room for waits (parts kept) and is given up after two days.
    /// </summary>
    public class BrowserRecordingRecoveryBackgroundService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(2);

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
                    await RunCycleAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Browser recording recovery failed; retrying next interval.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }

        private async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<(Guid Id, DateTime LastActivityAtUtc)> stalled;
            using (var scope = _scopeFactory.CreateScope())
            {
                stalled = await scope.ServiceProvider.GetRequiredService<ISessionService>()
                    .ListStalledBrowserRecordingsAsync(StalledAfter, 20, cancellationToken);
            }

            foreach (var (id, lastActivity) in stalled)
            {
                var abandoned = DateTime.UtcNow - lastActivity > GiveUpAfter;
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
                    var refusal = await scope.ServiceProvider.GetRequiredService<RecordingQuota>().RefusalAsync(cancellationToken);
                    if (refusal is not null)
                    {
                        if (abandoned) await sessions.CloseBrowserRecordingAsync(id, cancellationToken);
                        continue; // parts kept; tried again next cycle in case the plan gets room
                    }

                    if (await sessions.RecoverBrowserRecordingAsync(id, cancellationToken))
                    {
                        _logger.LogInformation("Recovered an interrupted browser recording ({UploadId}).", id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not recover browser recording {UploadId}.", id);
                    if (abandoned)
                    {
                        using var scope = _scopeFactory.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<ISessionService>().CloseBrowserRecordingAsync(id, cancellationToken);
                    }
                }
            }
        }
    }
}
