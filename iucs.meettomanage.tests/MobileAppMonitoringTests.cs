using iucs.meettomanage.api.Controllers;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Communication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class MobileAppMonitoringTests
{
    [Fact]
    public void DailyTrendPreservesEmptyDaysAndCountsEachFamilyOnce()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var activity = new[]
        {
            (first, new DateOnly(2026, 10, 24)),
            (first, new DateOnly(2026, 10, 24)),
            (second, new DateOnly(2026, 10, 26)),
        };

        var trend = PlatformTenantConfigurationController.BuildTrend(activity, now, 3);

        Assert.Equal(new[] { 1, 0, 1 }, trend.Select(x => x.ActiveFamilies));
        Assert.Equal(new DateOnly(2026, 10, 24), trend[0].Date);
        Assert.Equal(new DateOnly(2026, 10, 26), trend[2].Date);
    }

    [Fact]
    public void LatestVersionUsesNumericOrderingAndCurrentWindowOnly()
    {
        Assert.Equal("1.10.0", PlatformTenantConfigurationController.LatestVersion(new[] { "1.9.9", "1.10.0", "1.9.9" }));
        Assert.Null(PlatformTenantConfigurationController.LatestVersion(Array.Empty<string>()));
    }

    [Fact]
    public void DailyActivityHasOneRowPerUserAndUtcDate()
    {
        using var db = new MeetToManageDbContext(new DbContextOptionsBuilder<MeetToManageDbContext>()
            .UseNpgsql("Host=localhost;Database=none").Options);
        var entity = db.Model.FindEntityType(typeof(MobileAppDailyActivity))!;
        var index = entity.GetIndexes().Single(x => x.IsUnique && x.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { nameof(MobileAppDailyActivity.UserId), nameof(MobileAppDailyActivity.ActivityDate) }));

        Assert.Equal("ix_mobile_app_daily_activities_user_id_activity_date", index.GetDatabaseName());
        Assert.Equal("mobile_app_daily_activities", entity.GetTableName());
    }
}
