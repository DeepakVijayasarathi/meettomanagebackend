using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Services
{
    /// <summary>
    /// Automated reports: every Monday (07:00 UTC cycle) the admin team receives a
    /// KPI digest email generated from the live dashboard aggregates.
    /// </summary>
    public class ReportsDigestBackgroundService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ReportsDigestBackgroundService> _logger;

        public ReportsDigestBackgroundService(IServiceScopeFactory scopeFactory, ILogger<ReportsDigestBackgroundService> logger)
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
                    await RunMarketingAutomationAsync(now, stoppingToken);
                    if (now.DayOfWeek == DayOfWeek.Monday && now.Hour == 7)
                    {
                        await SendDigestAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Weekly report digest failed; retrying next cycle.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }

        private async Task RunMarketingAutomationAsync(DateTime now, CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var setting = await unitOfWork.Repository<iucs.meettomanage.domain.Entities.Marketing.MarketingReportSetting>()
                .TrackedQuery().OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
            if (setting is null) return;

            var operations = scope.ServiceProvider.GetRequiredService<IMarketingOperationsService>();
            await operations.ApplyRetentionAsync(cancellationToken);
            var email = scope.ServiceProvider.GetRequiredService<iucs.meettomanage.application.Common.Interfaces.IEmailSender>();
            var overdue = await unitOfWork.Repository<iucs.meettomanage.domain.Entities.Marketing.PlatformDemoRequest>().TrackedQuery()
                .Where(x => x.ArchivedAtUtc == null && x.NextFollowUpAtUtc < now && x.Status != StoreInquiryStatus.Converted && x.Status != StoreInquiryStatus.Closed
                    && (x.LastReminderSentAtUtc == null || x.LastReminderSentAtUtc < now.AddHours(-20)))
                .OrderBy(x => x.NextFollowUpAtUtc).Take(100).ToListAsync(cancellationToken);
            if (overdue.Count > 0 && !string.IsNullOrWhiteSpace(setting.RecipientEmail))
            {
                await email.SendAsync(setting.RecipientEmail, $"{overdue.Count} marketing follow-up(s) overdue",
                    $"<h2>Marketing follow-ups need attention</h2><p>{overdue.Count} active lead(s) are overdue. Open the Marketing Console → Demo Requests to assign or reschedule them.</p>", cancellationToken);
                foreach (var lead in overdue) lead.LastReminderSentAtUtc = now;
            }

            var localNow = now.AddMinutes(330);
            var due = setting.Enabled && localNow.Hour == 8 && (setting.LastSentAtUtc is null ||
                (setting.Frequency == "Daily" ? setting.LastSentAtUtc.Value.Date < now.Date : setting.LastSentAtUtc.Value < now.AddDays(-6)));
            if (due && !string.IsNullOrWhiteSpace(setting.RecipientEmail))
            {
                var analytics = scope.ServiceProvider.GetRequiredService<IMarketingAnalyticsService>();
                var dashboard = await analytics.GetDashboardAsync(setting.Frequency == "Daily" ? 1 : 7, 330, cancellationToken);
                var t = dashboard.Totals;
                await email.SendAsync(setting.RecipientEmail, $"{setting.Frequency} marketing performance report",
                    $"<h2>Marketing performance</h2><p><b>{t.Visitors}</b> visitors · <b>{t.DemoRequests}</b> demo requests · <b>{t.ConversionRate:0.#}%</b> visitor-to-demo · <b>{t.DemoPipelineConversionRate:0.#}%</b> demo-to-won.</p><p>Overdue follow-ups: {overdue.Count}.</p>", cancellationToken);
                setting.LastSentAtUtc = now;
            }
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        private async Task SendDigestAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var reports = scope.ServiceProvider.GetRequiredService<IReportsService>();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // Settings → Notifications → "Weekly summary digest" turns this sender off.
            if (!await NotificationToggles.IsEnabledAsync(unitOfWork, NotificationToggles.WeeklyDigest, cancellationToken))
            {
                _logger.LogInformation("Weekly KPI digest skipped: turned off in Settings → Notifications.");
                return;
            }

            var summary = await reports.GetDashboardSummaryAsync(cancellationToken);
            var tokens = new Dictionary<string, string>
            {
                ["TotalStudents"] = summary.TotalStudents.ToString(),
                ["ActiveStudents"] = summary.ActiveStudents.ToString(),
                ["RevenueCollected"] = summary.RevenueCollected.ToString("0.00"),
                ["RevenuePending"] = summary.RevenuePending.ToString("0.00"),
                ["TotalEnrollments"] = summary.TotalEnrollments.ToString(),
                ["ActiveBatches"] = summary.ActiveBatches.ToString(),
                ["DormantBatches"] = summary.DormantBatches.ToString(),
                ["OccupancyPercent"] = summary.BatchOccupancyPercent.ToString(),
                ["ConversionRate"] = summary.ConversionRatePercent.ToString(),
                ["RefundRate"] = summary.RefundRatePercent.ToString(),
                ["Utilization"] = summary.TeacherUtilizationSessionsPerTeacher.ToString(),
            };

            var admins = await unitOfWork.Repository<User>().Query()
                .Where(u => u.Role == UserRole.Admin && u.Status == UserStatus.Active)
                .ToListAsync(cancellationToken);
            foreach (var admin in admins)
            {
                await notifications.SendTemplatedEmailAsync(
                    admin.Id, admin.Email, NotificationType.General,
                    "weekly-kpi-digest", tokens, cancellationToken);
            }

            _logger.LogInformation("Weekly KPI digest sent to {Count} admin(s).", admins.Count);
        }
    }
}
