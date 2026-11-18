using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Dto.Auth;
using iucs.meettomanage.application.Helper;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests;

/// <summary>Self-service account deletion (required by Google Play / App Store).</summary>
public sealed class AccountDeletionTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly BcryptPasswordHasher _hasher = new();
    private readonly AuthService _auth;

    public AccountDeletionTests()
    {
        var audit = new AuditLogService(_db.UnitOfWork, _db.CurrentUser);
        var templates = new EmailTemplateService(_db.UnitOfWork, audit, new MemoryCache(new MemoryCacheOptions()));
        var notifications = new NotificationService(_db.UnitOfWork, new FakeEmailSender(), templates, new FakePushSender(), NullLogger<NotificationService>.Instance);
        _auth = new AuthService(_db.UnitOfWork, _hasher, new FakeTokenService(), audit, notifications,
            new ConfigurationBuilder().Build(), vault: null, deletion: new UserDeletionService(_db.UnitOfWork, audit));
    }

    public void Dispose() => _db.Dispose();

    private async Task<User> SeedParentAsync(string pin = "4821")
    {
        var user = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", _hasher.Hash(pin), UserRole.Parent);
        var parent = new ParentProfile { UserId = user.Id };
        _db.Context.AddRange(parent, new Child { ParentProfile = parent, FirstName = "Asha", LastName = "Kid" });
        await _db.Context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Parent_WithCorrectPin_IsDeletedWithTheirChildren()
    {
        var user = await SeedParentAsync();

        await _auth.DeleteOwnAccountAsync(user.Id, new DeleteAccountRequest { CurrentPin = "4821" });

        Assert.False(await _db.Context.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == user.Id));
        Assert.False(await _db.Context.Set<Child>().IgnoreQueryFilters().AnyAsync(c => c.FirstName == "Asha"));
    }

    [Fact]
    public async Task WrongPin_KeepsTheAccount_AndCountsTowardLockout()
    {
        var user = await SeedParentAsync();

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                _auth.DeleteOwnAccountAsync(user.Id, new DeleteAccountRequest { CurrentPin = "0000" }));

        // Locked: even the right PIN is refused until the lockout passes.
        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _auth.DeleteOwnAccountAsync(user.Id, new DeleteAccountRequest { CurrentPin = "4821" }));
        Assert.True(await _db.Context.Users.AnyAsync(u => u.Id == user.Id));
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Teacher)]
    public async Task StaffAccounts_CannotBeDeletedThisWay(UserRole role)
    {
        var user = await _db.SeedUserAsync($"s-{Guid.NewGuid():N}@test.com", _hasher.Hash("4821"), role);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _auth.DeleteOwnAccountAsync(user.Id, new DeleteAccountRequest { CurrentPin = "4821" }));
        Assert.True(await _db.Context.Users.AnyAsync(u => u.Id == user.Id));
    }
}
