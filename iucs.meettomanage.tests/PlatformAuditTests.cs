using System.Security.Claims;
using iucs.meettomanage.api.Saas;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class PlatformAuditTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<MasterDbContext> _options;
    private readonly PlatformAuditWriter _writer;

    private sealed class Factory(DbContextOptions<MasterDbContext> options) : IDbContextFactory<MasterDbContext>
    {
        public MasterDbContext CreateDbContext() => new(options);
    }

    public PlatformAuditTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<MasterDbContext>().UseSqlite(_connection).Options;
        using (var db = new MasterDbContext(_options)) db.Database.EnsureCreated();
        _writer = new PlatformAuditWriter(new Factory(_options), NullLogger<PlatformAuditWriter>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private static ActionExecutingContext Context(string method, string path, bool platform, params (string Key, string Value)[] route)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            platform ? [new Claim("purpose", "platform-admin"), new Claim(ClaimTypes.Name, "ops@example.test")] : [new Claim(ClaimTypes.Name, "teacher@example.test")],
            "test"));
        var data = new RouteData();
        data.Values["controller"] = "Platform";
        data.Values["action"] = "SetStatus";
        foreach (var (key, value) in route) data.Values[key] = value;
        return new ActionExecutingContext(new ActionContext(http, data, new ControllerActionDescriptor()), [], new Dictionary<string, object?>(), new object());
    }

    private static ActionExecutionDelegate Next(ActionExecutingContext context, IActionResult? result = null, Exception? failure = null) =>
        () => Task.FromResult(new ActionExecutedContext(context, [], new object()) { Result = result, Exception = failure });

    private List<iucs.meettomanage.domain.Entities.Saas.PlatformAuditLog> Rows()
    {
        using var db = new MasterDbContext(_options);
        return db.PlatformAuditLogs.AsNoTracking().ToList();
    }

    [Fact]
    public async Task RecordsAStateChangeMadeByAnOperatorAndNamesTheClient()
    {
        var tenant = Guid.NewGuid();
        var context = Context("PUT", $"/api/platform/tenants/{tenant}/status", true, ("id", tenant.ToString()));
        await new PlatformAuditFilter(_writer).OnActionExecutionAsync(context, Next(context, new OkResult()));

        var row = Assert.Single(Rows());
        Assert.Equal("ops@example.test", row.Actor);
        Assert.Equal("Platform.SetStatus", row.Action);
        Assert.Equal("PUT", row.HttpMethod);
        Assert.Equal(tenant, row.TenantId);
        Assert.Equal(200, row.StatusCode);
    }

    [Fact]
    public async Task DoesNotTreatAPlanIdAsAClient()
    {
        var plan = Guid.NewGuid();
        var context = Context("PUT", $"/api/platform/plans/{plan}", true, ("id", plan.ToString()));
        await new PlatformAuditFilter(_writer).OnActionExecutionAsync(context, Next(context, new OkResult()));
        Assert.Null(Assert.Single(Rows()).TenantId);
    }

    [Fact]
    public async Task RecordsTheStatusOfAFailedAction()
    {
        var context = Context("POST", "/api/platform/tenants", true);
        await new PlatformAuditFilter(_writer).OnActionExecutionAsync(context, Next(context, failure: new ConflictException("Already exists.")));
        var row = Assert.Single(Rows());
        Assert.Equal(409, row.StatusCode);
        Assert.Equal("Already exists.", row.Detail);
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("POST", false)]
    public async Task IgnoresReadsAndNonOperators(string method, bool platform)
    {
        var context = Context(method, "/api/platform/tenants", platform);
        await new PlatformAuditFilter(_writer).OnActionExecutionAsync(context, Next(context, new OkResult()));
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task AnAuditOutageNeverBreaksTheOperatorsRequest()
    {
        await _connection.DisposeAsync();
        await _writer.WriteAsync("ops@example.test", "Platform.SetStatus", "PUT", "/x", 200, null, null);
    }
}
