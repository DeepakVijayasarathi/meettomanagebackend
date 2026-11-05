using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Entities.Sessions;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Xunit;

namespace iucs.meettomanage.tests;

/// <summary>The family app's "Stars &amp; badges" screen: a parent sees only their own child's awards.</summary>
public sealed class ChildRewardsTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    // GetChildRewardsAsync never touches the session service, so the live-class dependency is unused here.
    private GamificationService CreateService() => new(_db.UnitOfWork, null!);

    private async Task<(User Parent, Child Child)> SeedFamilyAsync(string email, string first, string last)
    {
        var user = await _db.SeedUserAsync(email, "x");
        var profile = new ParentProfile { UserId = user.Id };
        var child = new Child { ParentProfile = profile, FirstName = first, LastName = last };
        _db.Context.AddRange(profile, child);
        await _db.Context.SaveChangesAsync();
        return (user, child);
    }

    private async Task AwardAsync(string participant, AwardKind kind, int points = 1, string? label = null)
    {
        _db.Context.Add(new StudentAward { ParticipantName = participant, Kind = kind, Points = points, Label = label });
        await _db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task TotalsStarsAndListsBadgesForTheOwnChildOnly()
    {
        var (parent, child) = await SeedFamilyAsync("a@example.com", "Aarav", "Sharma");
        await SeedFamilyAsync("b@example.com", "Zoya", "Khan");
        await AwardAsync("Aarav Sharma", AwardKind.Star, 2);
        await AwardAsync("aarav sharma", AwardKind.Star, 1); // names match regardless of case
        await AwardAsync("Aarav Sharma", AwardKind.Badge, 0, "Reading Hero");
        await AwardAsync("Zoya Khan", AwardKind.Star, 9);

        var rewards = await CreateService().GetChildRewardsAsync(parent.Id, child.Id);

        Assert.Equal(3, rewards.TotalStars);
        Assert.Equal(3, rewards.StarsLast30Days);
        Assert.Equal("Reading Hero", Assert.Single(rewards.Rewards).Label);
        Assert.Equal(3, rewards.NextMilestoneStars);
    }

    [Fact]
    public async Task TheSameBadgeEarnedTwiceIsShownOnce()
    {
        var (parent, child) = await SeedFamilyAsync("a@example.com", "Aarav", "Sharma");
        await AwardAsync("Aarav Sharma", AwardKind.Badge, 0, "Reading Hero");
        await AwardAsync("Aarav Sharma", AwardKind.Badge, 0, "reading hero");

        var rewards = await CreateService().GetChildRewardsAsync(parent.Id, child.Id);

        Assert.Single(rewards.Rewards);
    }

    [Fact]
    public async Task AnotherFamilysChildLooksLikeAMissingOne()
    {
        var (_, child) = await SeedFamilyAsync("a@example.com", "Aarav", "Sharma");
        var (stranger, _) = await SeedFamilyAsync("b@example.com", "Zoya", "Khan");

        await Assert.ThrowsAsync<NotFoundException>(() => CreateService().GetChildRewardsAsync(stranger.Id, child.Id));
    }

    [Fact]
    public async Task AChildWithNoAwardsGetsAnEmptyResultNotAnError()
    {
        var (parent, child) = await SeedFamilyAsync("a@example.com", "Aarav", "Sharma");

        var rewards = await CreateService().GetChildRewardsAsync(parent.Id, child.Id);

        Assert.Equal(0, rewards.TotalStars);
        Assert.Empty(rewards.Rewards);
    }
}
