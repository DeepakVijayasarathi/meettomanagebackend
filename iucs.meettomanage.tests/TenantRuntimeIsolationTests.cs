using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class TenantRuntimeIsolationTests
{
    [Fact]
    public async Task AmbientUrlBindingsDoNotLeakBetweenConcurrentRequests()
    {
        async Task<string> Url(string code)
        {
            using var tenant = TenantExecution.Enter(Guid.NewGuid(), code);
            await Task.Yield();
            return TenantExecution.Url("https://example.test");
        }
        var urls = await Task.WhenAll(Url("academy-one"), Url("academy-two"));
        Assert.Equal("https://example.test/t/academy-one", urls[0]);
        Assert.Equal("https://example.test/t/academy-two", urls[1]);
        Assert.Null(TenantExecution.Current);
    }

    [Fact]
    public void PresenceAndRoomGroupsAreIsolatedEvenWhenBusinessIdsMatch()
    {
        var tracker = new ClassroomPresenceTracker();
        var session = Guid.NewGuid().ToString();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        string firstGroup;
        using (TenantExecution.Enter(first, "academy-one"))
        {
            tracker.UserJoined(session, "connection-one", Guid.NewGuid(), "Teacher A", "teacher");
            firstGroup = api.Hubs.ClassroomHub.GroupFor(Guid.Parse(session));
        }
        using (TenantExecution.Enter(second, "academy-two"))
        {
            Assert.Equal(0, tracker.TotalConnectedUsers);
            tracker.UserJoined(session, "connection-two", Guid.NewGuid(), "Teacher B", "teacher");
            Assert.Single(tracker.GetLiveConnections());
            Assert.Equal("Teacher B", tracker.GetLiveConnections()[0].Name);
            Assert.NotEqual(firstGroup, api.Hubs.ClassroomHub.GroupFor(Guid.Parse(session)));
        }
        using (TenantExecution.Enter(first, "academy-one"))
        {
            Assert.Single(tracker.GetLiveConnections());
            Assert.Equal("Teacher A", tracker.GetLiveConnections()[0].Name);
        }
    }

    [Fact]
    public void SignedLocalFileUrlsCannotBeReplayedForAnotherTenant()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "shared-app-signing-key-for-test-tenants",
            ["Api:BaseUrl"] = "https://api.example.test",
            ["Storage:LocalPath"] = Path.Combine(Path.GetTempPath(), "mtm-isolation-test"),
        }).Build();
        LocalFileStorage first;
        string url;
        using (TenantExecution.Enter(Guid.NewGuid(), "academy-one"))
        {
            first = new LocalFileStorage(new TestEnvironment(), config, new HttpContextAccessor());
            url = first.GetReadUrl("worksheet.pdf", TimeSpan.FromMinutes(1), "application/pdf");
        }
        var query = QueryHelpers.ParseQuery(new Uri(url).Query);
        var expiry = long.Parse(query["exp"].ToString());
        Assert.True(first.IsValidReadSignature("worksheet.pdf", expiry, query["ct"], query["sig"]));
        using (TenantExecution.Enter(Guid.NewGuid(), "academy-two"))
        {
            var second = new LocalFileStorage(new TestEnvironment(), config, new HttpContextAccessor());
            Assert.NotEqual(first.RootPath, second.RootPath);
            Assert.False(second.IsValidReadSignature("worksheet.pdf", expiry, query["ct"], query["sig"]));
        }
        Assert.Contains("/t/academy-one/", url);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
