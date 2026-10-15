using System.Reflection;
using System.Security.Claims;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.api.Controllers;
using iucs.meettomanage.api.Middleware;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace iucs.meettomanage.tests
{
    /// <summary>
    /// The marketing console is the platform owner's: academy staff (Admin included) can't reach
    /// its endpoints, and a Marketing login can't reach anything else.
    /// </summary>
    public class MarketingConsoleAccessTests
    {
        private static HttpContext Request(string path, string? role)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Response.Body = new MemoryStream();
            if (role is not null)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
            }
            return context;
        }

        private static async Task<(bool PassedThrough, int Status)> Run(string path, string? role)
        {
            var passed = false;
            var middleware = new MarketingScopeMiddleware(_ =>
            {
                passed = true;
                return Task.CompletedTask;
            });
            var context = Request(path, role);
            await middleware.InvokeAsync(context);
            return (passed, context.Response.StatusCode);
        }

        [Theory]
        [InlineData("/api/marketing/demo-requests")]
        [InlineData("/api/marketing/settings")]
        [InlineData("/api/blog/admin")]
        [InlineData("/api/auth/me")]
        [InlineData("/api/auth/change-pin")]
        [InlineData("/api/settings/public")]
        public async Task MarketingLogin_CanReachTheConsoleApis(string path)
        {
            var (passed, _) = await Run(path, nameof(UserRole.Marketing));
            Assert.True(passed);
        }

        [Theory]
        [InlineData("/api/users")]
        [InlineData("/api/settings")]
        [InlineData("/api/menus/mine")]
        [InlineData("/api/billing/invoices")]
        [InlineData("/api/sessions")]
        [InlineData("/hubs/classroom")]
        public async Task MarketingLogin_IsRefusedEverythingElse(string path)
        {
            var (passed, status) = await Run(path, nameof(UserRole.Marketing));
            Assert.False(passed);
            Assert.Equal(StatusCodes.Status403Forbidden, status);
        }

        [Theory]
        [InlineData(nameof(UserRole.Admin))]
        [InlineData(nameof(UserRole.SubAdmin))]
        [InlineData(null)]
        public async Task OtherRequests_AreUntouchedByTheScopeGuard(string? role)
        {
            var (passed, _) = await Run("/api/users", role);
            Assert.True(passed);
        }

        /// <summary>
        /// Admin passes every HasPermission check, so a marketing endpoint gated that way would be
        /// open to every academy Admin. Each one must require the Marketing role instead.
        /// </summary>
        [Theory]
        [InlineData(typeof(PlatformDemoRequestsAdminController))]
        [InlineData(typeof(BlogAdminController))]
        [InlineData(typeof(MarketingSettingsController))]
        public void MarketingAdminEndpoints_RequireTheMarketingRole_NotAPermission(Type controller)
        {
            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .ToList();
            Assert.NotEmpty(actions);

            var classRoles = controller.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Roles);
            foreach (var action in actions)
            {
                Assert.Empty(action.GetCustomAttributes<HasPermissionAttribute>());
                Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
                var roles = classRoles.Concat(action.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Roles));
                Assert.Contains(nameof(UserRole.Marketing), roles);
            }
        }
    }
}
