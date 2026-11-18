using System.Security.Claims;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common.Exceptions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class TenantRequestMiddlewareTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/HEALTH")]
    [InlineData("/health/ready")]
    [InlineData("/health/live")]
    [InlineData("/api/platform/monitoring")]
    [InlineData("/api/status")]
    public void Platform_and_health_paths_do_not_require_a_tenant(string path)
    {
        Assert.True(TenantRequestMiddleware.IsTenantOptionalPath(path));
    }

    [Theory]
    [InlineData("/healthcheck")]
    [InlineData("/api/students")]
    [InlineData("/api/monitoring/summary")]
    public void Business_paths_still_require_tenant_resolution(string path)
    {
        Assert.False(TenantRequestMiddleware.IsTenantOptionalPath(path));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Bearer"));

    [Fact]
    public void Only_a_signed_in_platform_operator_counts_as_a_platform_principal()
    {
        Assert.True(TenantRequestMiddleware.IsPlatformPrincipal(Principal(new Claim("purpose", "platform-admin"), new Claim(ClaimTypes.Role, "PlatformAdmin"))));
        Assert.False(TenantRequestMiddleware.IsPlatformPrincipal(Principal(new Claim(ClaimTypes.Role, "PlatformAdmin"))));
        Assert.False(TenantRequestMiddleware.IsPlatformPrincipal(Principal(new Claim(ClaimTypes.Role, "Admin"), new Claim("tenant_id", Guid.NewGuid().ToString()))));
        Assert.False(TenantRequestMiddleware.IsPlatformPrincipal(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public async Task A_platform_token_cannot_reach_a_tenant_business_api()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/students";
        http.Request.Method = "GET";
        http.Items["TenantPathCode"] = "academy-one";
        http.User = Principal(new Claim("purpose", "platform-admin"), new Claim(ClaimTypes.Role, "PlatformAdmin"));
        var reached = false;
        var middleware = new TenantRequestMiddleware(_ => { reached = true; return Task.CompletedTask; });

        // The guard runs before any tenant lookup, so neither resolver dependency is touched.
        await Assert.ThrowsAsync<ForbiddenException>(() => middleware.InvokeAsync(http, null!, new TenantContext()));
        Assert.False(reached);
    }
}
