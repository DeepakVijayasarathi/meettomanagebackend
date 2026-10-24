using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Tenancy;
using iucs.meettomanage.domain.Entities.Users;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class TenantResolutionTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MasterDbContext _master;
    private readonly TenantContext _current = new();
    private readonly TenantResolver _resolver;
    private readonly Tenant _first = new()
    {
        Code = "academy-one", Name = "Academy One", DatabaseName = "mt_" + Guid.NewGuid().ToString("N"),
        Status = TenantStatus.Active,
    };
    private readonly Tenant _second = new()
    {
        Code = "academy-two", Name = "Academy Two", DatabaseName = "mt_" + Guid.NewGuid().ToString("N"),
        Status = TenantStatus.Active,
    };

    public TenantResolutionTests()
    {
        _connection.Open();
        _master = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>().UseSqlite(_connection).Options);
        _master.Database.EnsureCreated();
        _master.Tenants.AddRange(_first, _second);
        _master.LoginIdentifiers.Add(new TenantLoginIdentifier
        {
            Identifier = "email:one@example.test",
            Account = new TenantLoginAccount { Tenant = _first, UserId = Guid.NewGuid() },
        });
        _master.SaveChanges();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TenantServer"] = "Host=db.internal;Username=app;Password=test-secret;Database=postgres",
        }).Build();
        _resolver = new TenantResolver(_master, _current, configuration);
    }

    [Fact]
    public async Task EmailResolvesTenantAndDatabaseWithoutClientSuppliedTenant()
    {
        await _resolver.ResolveLoginAsync(" ONE@EXAMPLE.TEST ");
        Assert.Equal(_first.Id, _current.TenantId);
        Assert.Equal(_first.Code, _current.TenantCode);
        var connection = new NpgsqlConnectionStringBuilder(_current.RequireConnectionString());
        Assert.Equal(_first.DatabaseName, connection.Database);
        Assert.Equal("db.internal", connection.Host);
        Assert.False(connection.IncludeErrorDetail);
    }

    [Fact]
    public async Task ScopeCannotSwitchToAnotherTenantsDatabase()
    {
        await _resolver.ResolveTokenAsync(_first.Id, _first.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _resolver.ResolveTokenAsync(_second.Id, _second.Code));
        Assert.Equal(_first.DatabaseName, new NpgsqlConnectionStringBuilder(_current.RequireConnectionString()).Database);
    }

    [Fact]
    public async Task TokenIdAndCodeMustReferToSameTenant()
    {
        await Assert.ThrowsAsync<UnauthorizedException>(() => _resolver.ResolveTokenAsync(_first.Id, _second.Code));
        Assert.Throws<InvalidOperationException>(() => _current.RequireConnectionString());
    }

    [Theory]
    [InlineData(TenantStatus.Provisioning)]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Failed)]
    public async Task NonActiveTenantsCannotResolve(TenantStatus status)
    {
        _first.Status = status;
        await _master.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedException>(() => _resolver.ResolveLoginAsync("one@example.test"));
        Assert.Null(_current.TenantId);
    }

    [Fact]
    public async Task UnknownLoginDoesNotFallBackToAnotherDatabase()
    {
        await Assert.ThrowsAsync<UnauthorizedException>(() => _resolver.ResolveLoginAsync("unknown@example.test"));
        Assert.Null(_current.TenantId);
    }

    [Fact]
    public async Task AllTokenTypesCarryTenantAndNoConnectionCredentials()
    {
        await _resolver.ResolveTokenAsync(_first.Id, _first.Code);
        var tokens = new api.Auth.JwtTokenService(Microsoft.Extensions.Options.Options.Create(new api.Auth.JwtOptions
        {
            Issuer = "test", Audience = "test", SigningKey = "test-signing-key-at-least-thirty-two-characters", AccessTokenMinutes = 30,
        }), _current);
        var user = new domain.Entities.Users.User { Id = Guid.NewGuid(), Email = "one@example.test", FirstName = "Test", LastName = "User" };
        var session = Guid.NewGuid();
        var expiry = DateTime.UtcNow.AddMinutes(5);
        var results = new[]
        {
            tokens.CreateToken(user, []), tokens.CreateViewAsToken(user, [], Guid.NewGuid(), expiry),
            tokens.CreateRecordingObserverHubToken(session, expiry), tokens.CreateGuestJoinToken(session, null, expiry),
            tokens.CreateGuestClassroomHubToken(session, null, "Guest", expiry),
        };
        foreach (var result in results)
        {
            var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
            Assert.Contains(jwt.Claims, x => x.Type == TenantContext.TenantIdClaim && x.Value == _first.Id.ToString());
            Assert.Contains(jwt.Claims, x => x.Type == TenantContext.TenantCodeClaim && x.Value == _first.Code);
            Assert.DoesNotContain("test-secret", jwt.Payload.SerializeToJson());
            Assert.DoesNotContain(_first.DatabaseName, jwt.Payload.SerializeToJson());
        }
    }

    [Fact]
    public void BothDatabaseModelsHaveMatchingMigrations()
    {
        using var master = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_design;Username=postgres").Options);
        using var business = new MeetToManageDbContext(new DbContextOptionsBuilder<MeetToManageDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_design;Username=postgres").Options);
        Assert.False(master.Database.HasPendingModelChanges());
        Assert.False(business.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task GlobalIdentifierCannotBeRegisteredByAnotherTenant()
    {
        _master.ChangeTracker.Clear();
        _master.LoginIdentifiers.Add(new TenantLoginIdentifier
        {
            Identifier = "email:one@example.test",
            Account = new TenantLoginAccount { TenantId = _second.Id, UserId = Guid.NewGuid() },
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => _master.SaveChangesAsync());
    }

    [Fact]
    public async Task AliasCannotAssociateExistingUserWithAnotherTenant()
    {
        var userId = await _master.LoginAccounts.Select(x => x.UserId).SingleAsync();
        _master.LoginIdentifiers.Add(new TenantLoginIdentifier
        {
            Identifier = "phone:9876543210", UserId = userId, TenantId = _second.Id,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => _master.SaveChangesAsync());
    }

    [Theory]
    [InlineData("+91 98765 43210", "phone:9876543210")]
    [InlineData("09876543210", "phone:9876543210")]
    [InlineData(" Admin@Example.test ", "email:admin@example.test")]
    public void LoginNormalizationIsConsistent(string input, string expected) =>
        Assert.Equal(expected, TenantResolver.NormalizeIdentifier(input));

    [Theory]
    [InlineData(" Academy.Example.COM. ", "academy.example.com")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("https://academy.example.com", null)]
    [InlineData("academy.example.com/path", null)]
    [InlineData("127.0.0.1", null)]
    public void CustomDomainsAreCanonicalDnsHostnames(string input, string? expected)
    {
        if (expected is null) Assert.Throws<DomainValidationException>(() => TenantDomainResolver.Normalize(input));
        else Assert.Equal(expected, TenantDomainResolver.Normalize(input));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mt_bad;Password=injected")]
    [InlineData("mt_../../another")]
    public void RegistryDatabaseNamesCannotInjectConnectionOptions(string name) =>
        Assert.Throws<InvalidOperationException>(() => TenantResolver.BuildConnectionString("Host=localhost;Username=app", name));

    [Theory]
    [InlineData("meettomanagedemo", true)]
    [InlineData("mt_1c2d9f5d52c34f2aa6ecee4b3f017154", true)]
    [InlineData("client_db_2", true)]
    [InlineData("postgres", false)]
    [InlineData("template1", false)]
    [InlineData("Mixed", false)]
    [InlineData("1starts_with_digit", false)]
    [InlineData("has-dash", false)]
    [InlineData("quote\"; DROP DATABASE x;--", false)]
    [InlineData("", false)]
    public void OperatorSuppliedDatabaseNamesAreStrictIdentifiers(string name, bool valid) =>
        Assert.Equal(valid, TenantResolver.IsValidDatabaseName(name));

    private sealed class SharedFactory(SqliteConnection connection) : IDbContextFactory<MasterDbContext>
    {
        public MasterDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MasterDbContext>().UseSqlite(connection).Options);
    }

    private async Task<LoginDirectory> DirectoryForSecondTenantAsync()
    {
        await _resolver.ResolveCodeAsync(_second.Code);
        return new LoginDirectory(new SharedFactory(_connection), _current);
    }

    [Fact]
    public async Task SyncSkipsAPhoneSharedByTwoUsersAndKeepsTheirEmails()
    {
        var directory = await DirectoryForSecondTenantAsync();
        var first = new User { Id = Guid.NewGuid(), Email = "a@example.test", Phone = "+91 98765 43210" };
        var second = new User { Id = Guid.NewGuid(), Email = "b@example.test", Phone = "9876543210" };
        var skipped = new List<string>();

        await directory.ReserveAsync([first, second], default, skipPhoneConflicts: true, skipped);

        Assert.Equal(["phone:9876543210"], skipped);
        var identifiers = await _master.LoginIdentifiers.AsNoTracking().Select(x => x.Identifier).ToListAsync();
        Assert.Contains("email:a@example.test", identifiers);
        Assert.Contains("email:b@example.test", identifiers);
        Assert.Single(identifiers, x => x == "phone:9876543210");
    }

    [Fact]
    public async Task OrdinaryWritesStillRejectASharedPhone()
    {
        var directory = await DirectoryForSecondTenantAsync();
        await directory.ReserveAsync([new User { Id = Guid.NewGuid(), Email = "a@example.test", Phone = "9876543210" }], default);
        await Assert.ThrowsAsync<ConflictException>(() => directory.ReserveAsync(
            [new User { Id = Guid.NewGuid(), Email = "b@example.test", Phone = "9876543210" }], default));
    }

    [Fact]
    public async Task SyncStillRejectsASharedEmail()
    {
        var directory = await DirectoryForSecondTenantAsync();
        await Assert.ThrowsAsync<ConflictException>(() => directory.ReserveAsync(
            [new User { Id = Guid.NewGuid(), Email = "one@example.test" }], default, skipPhoneConflicts: true));
    }

    [Theory]
    [InlineData("one@example.test", true)]
    [InlineData("free@example.test", false)]
    public async Task MarketingSeedIsBlankedWhenAnotherClientOwnsTheEmail(string email, bool blanked)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:MarketingEmail"] = email, ["Seed:MarketingPin"] = "1234",
        }).Build();
        var overrides = await TenantServices.MarketingSeedOverridesAsync(_master, configuration);
        Assert.Equal(blanked, overrides.ContainsKey("Seed:MarketingEmail"));
        if (blanked) Assert.Equal("", overrides["Seed:MarketingEmail"]);
    }

    public void Dispose()
    {
        _master.Dispose();
        _connection.Dispose();
    }
}
