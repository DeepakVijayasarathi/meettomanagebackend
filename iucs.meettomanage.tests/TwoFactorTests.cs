using System.Text;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Dto.Auth;
using iucs.meettomanage.application.Helper;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class TwoFactorTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly BcryptPasswordHasher _hasher = new();
    private readonly AuthService _auth;

    public TwoFactorTests()
    {
        var audit = new AuditLogService(_db.UnitOfWork, _db.CurrentUser);
        var templates = new EmailTemplateService(_db.UnitOfWork, audit, new MemoryCache(new MemoryCacheOptions()));
        var notifications = new NotificationService(_db.UnitOfWork, new FakeEmailSender(), templates, new FakePushSender(), NullLogger<NotificationService>.Instance);
        var vault = new AesGcmPinVault(Microsoft.Extensions.Options.Options.Create(new PinVaultOptions { Key = "test-key-test-key-test-key-test-key" }));
        _auth = new AuthService(_db.UnitOfWork, _hasher, new FakeTokenService(), audit, notifications, new ConfigurationBuilder().Build(), vault);
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    // RFC 6238 Appendix B (SHA-1 seed), truncated to 6 digits.
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Totp_MatchesTheRfcTestVectors(long unixSeconds, string expected)
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(expected, Totp.Code(secret, DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime));
    }

    [Fact]
    public void Totp_Base32RoundTripsAndToleratesOneStepOfDrift()
    {
        var secret = Totp.NewSecret();
        Assert.Equal(secret, Totp.FromBase32(Totp.ToBase32(secret)));
        var now = DateTime.UtcNow;
        var previous = Totp.Code(secret, now.AddSeconds(-30));
        Assert.NotNull(Totp.Verify(secret, previous, now));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, now.AddMinutes(-5)), now));
        var step = Totp.Verify(secret, Totp.Code(secret, now), now);
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, now), now, lastUsedStep: step));
        Assert.Null(Totp.Verify(secret, "12ab", now));
    }

    [Fact]
    public async Task Admin_TurnsOnTwoStepSignIn_AndThenNeedsACodeToSignIn()
    {
        var admin = await _db.SeedUserAsync($"a-{Guid.NewGuid():N}@test.com", _hasher.Hash("1234"), UserRole.Admin);

        var setup = await _auth.BeginTwoFactorSetupAsync(admin.Id);
        Assert.StartsWith("otpauth://totp/", setup.OtpAuthUri);
        var secret = Totp.FromBase32(setup.Secret);
        await Assert.ThrowsAsync<DomainValidationException>(() => _auth.EnableTwoFactorAsync(admin.Id, "000000"));
        var recovery = await _auth.EnableTwoFactorAsync(admin.Id, Totp.Code(secret, DateTime.UtcNow));
        Assert.Equal(8, recovery.RecoveryCodes.Count);
        Assert.True((await _auth.GetTwoFactorStatusAsync(admin.Id)).Enabled);

        // The PIN alone no longer gives a session.
        var first = await _auth.LoginAsync(new LoginRequest { Email = admin.Email, Pin = "1234" });
        Assert.True(first.RequiresTwoFactor);
        Assert.Equal(string.Empty, first.AccessToken);
        Assert.False(string.IsNullOrEmpty(first.TwoFactorChallenge));

        await Assert.ThrowsAsync<UnauthorizedException>(() => _auth.CompleteTwoFactorLoginAsync(
            new CompleteTwoFactorLoginRequest { Challenge = first.TwoFactorChallenge!, Code = "000000" }));

        // A recovery code works once, then the challenge is spent.
        var session = await _auth.CompleteTwoFactorLoginAsync(
            new CompleteTwoFactorLoginRequest { Challenge = first.TwoFactorChallenge!, Code = recovery.RecoveryCodes[0].ToLowerInvariant() });
        Assert.Equal(admin.Id, session.User.Id);
        Assert.False(session.RequiresTwoFactor);
        await Assert.ThrowsAsync<UnauthorizedException>(() => _auth.CompleteTwoFactorLoginAsync(
            new CompleteTwoFactorLoginRequest { Challenge = first.TwoFactorChallenge!, Code = recovery.RecoveryCodes[1] }));

        var again = await _auth.LoginAsync(new LoginRequest { Email = admin.Email, Pin = "1234" });
        await Assert.ThrowsAsync<UnauthorizedException>(() => _auth.CompleteTwoFactorLoginAsync(
            new CompleteTwoFactorLoginRequest { Challenge = again.TwoFactorChallenge!, Code = recovery.RecoveryCodes[0] }));
        Assert.Equal(7, (await _auth.GetTwoFactorStatusAsync(admin.Id)).RecoveryCodesLeft);
    }

    [Fact]
    public async Task WrongCodes_LockTheAccount_AndAdminsCanResetALostDevice()
    {
        var admin = await _db.SeedUserAsync($"a-{Guid.NewGuid():N}@test.com", _hasher.Hash("1234"), UserRole.Admin);
        var teacher = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", _hasher.Hash("1234"), UserRole.Teacher);
        var setup = await _auth.BeginTwoFactorSetupAsync(teacher.Id);
        await _auth.EnableTwoFactorAsync(teacher.Id, Totp.Code(Totp.FromBase32(setup.Secret), DateTime.UtcNow));

        var challenge = (await _auth.LoginAsync(new LoginRequest { Email = teacher.Email, Pin = "1234" })).TwoFactorChallenge!;
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<UnauthorizedException>(() => _auth.CompleteTwoFactorLoginAsync(
                new CompleteTwoFactorLoginRequest { Challenge = challenge, Code = "111111" }));
        var locked = await Assert.ThrowsAsync<UnauthorizedException>(() => _auth.LoginAsync(new LoginRequest { Email = teacher.Email, Pin = "1234" }));
        Assert.Contains("Too many", locked.Message);

        await Assert.ThrowsAsync<DomainValidationException>(() => _auth.ResetTwoFactorAsync(teacher.Id, teacher.Id));
        await _auth.ResetTwoFactorAsync(admin.Id, teacher.Id);
        Assert.False((await _auth.GetTwoFactorStatusAsync(teacher.Id)).Enabled);
    }

    [Fact]
    public async Task FamilyAccounts_CannotTurnItOn_AndUnaffectedAccountsSignInAsBefore()
    {
        var parent = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", _hasher.Hash("1234"), UserRole.Parent);
        Assert.False((await _auth.GetTwoFactorStatusAsync(parent.Id)).Available);
        await Assert.ThrowsAsync<ForbiddenException>(() => _auth.BeginTwoFactorSetupAsync(parent.Id));

        var login = await _auth.LoginAsync(new LoginRequest { Email = parent.Email, Pin = "1234" });
        Assert.False(login.RequiresTwoFactor);
        Assert.Equal(parent.Id, login.User.Id);
    }
}
