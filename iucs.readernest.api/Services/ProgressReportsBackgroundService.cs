using iucs.readernest.application.Services;

namespace iucs.readernest.api.Services
{
    /// <summary>
    /// On the 1st of every month, seeds an empty draft progress report for every active
    /// child so staff open this month's Progress Reports list already populated with one
    /// row per child, ready to write and send. Existing drafts/sent reports are untouched.
    /// </summary>
    public class ProgressReportsBackgroundService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ProgressReportsBackgroundService> _logger;

        public ProgressReportsBackgroundService(IServiceScopeFactory scopeFactory, ILogger<ProgressReportsBackgroundService> logger)
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
                    var now = DateTime.UtcNow;
                    if (now.Day == 1 && now.Hour == 6)
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var progressReports = scope.ServiceProvider.GetRequiredService<IProgressReportService>();
                        var created = await progressReports.EnsureMonthlyDraftsAsync(now.Year, now.Month, stoppingToken);
                        _logger.LogInformation("Progress reports: seeded {Count} draft(s) for {Year}-{Month:D2}.", created, now.Year, now.Month);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Monthly progress-report seeding failed; retrying next cycle.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
    }
}
