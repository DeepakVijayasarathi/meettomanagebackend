using iucs.meettomanage.api.Tenancy;
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
}
