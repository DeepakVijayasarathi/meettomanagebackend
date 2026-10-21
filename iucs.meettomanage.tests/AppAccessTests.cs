using iucs.meettomanage.api.Controllers;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class AppAccessTests
{
    private static IHeaderDictionary Headers(string? client)
    {
        var headers = new HeaderDictionary();
        if (client is not null) headers[AppAccessPolicy.ClientHeader] = client;
        return headers;
    }

    [Theory]
    [InlineData("parent-app", false, true)]
    [InlineData("Parent-App", false, true)]
    [InlineData(" parent-app ", false, true)]
    [InlineData("parent-app", true, false)]
    [InlineData(null, false, false)]
    [InlineData("web", false, false)]
    [InlineData("", false, false)]
    public void OnlyTheParentAppIsGatedAndOnlyWhenNotGranted(string? client, bool granted, bool denied) =>
        Assert.Equal(denied, AppAccessPolicy.IsDenied(Headers(client), granted));

    [Theory]
    [InlineData("https://meettomanage.cloud", true)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("meettomanage.cloud", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAbsoluteWebAddressesAreSentToTheApp(string? value, bool allowed) =>
        Assert.Equal(allowed, AppConfigController.IsHttpUrl(value));

    [Fact]
    public async Task ATenantCreatedWithoutAppAccessStaysWithoutIt()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MasterDbContext>().UseSqlite(connection).Options;
        await using (var db = new MasterDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Code = "no-app", Name = "No App", DatabaseName = "mt_a", AppAccessGranted = false });
            db.Tenants.Add(new Tenant { Code = "with-app", Name = "With App", DatabaseName = "mt_b" });
            await db.SaveChangesAsync();
        }
        await using var read = new MasterDbContext(options);
        Assert.False((await read.Tenants.SingleAsync(x => x.Code == "no-app")).AppAccessGranted);
        Assert.True((await read.Tenants.SingleAsync(x => x.Code == "with-app")).AppAccessGranted);
    }

    [Fact]
    public void TheMasterColumnIsSnakeCase()
    {
        using var db = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql("Host=localhost;Database=none").Options);
        var property = db.Model.FindEntityType(typeof(Tenant))!.FindProperty(nameof(Tenant.AppAccessGranted))!;
        Assert.Equal("app_access_granted", property.GetColumnName());
    }
}
