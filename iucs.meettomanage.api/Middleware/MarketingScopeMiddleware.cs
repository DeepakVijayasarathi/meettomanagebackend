using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Middleware
{
    /// <summary>
    /// Keeps a Marketing-role session (the platform owner's marketing-site team, see
    /// <see cref="UserRole.Marketing"/>) inside the marketing console: it may sign in, manage its
    /// own PIN, read public settings and use the marketing/blog endpoints — and nothing else. Many
    /// academy endpoints are plain [Authorize] (any signed-in user), so without this a marketing
    /// login could read academy data it has no business seeing. The other direction (academy staff,
    /// including Admin, kept out of marketing) is enforced on the marketing controllers themselves.
    /// Runs after authentication, so the role is already the one re-read from the database.
    /// </summary>
    public class MarketingScopeMiddleware
    {
        private static readonly string[] AllowedPrefixes =
        [
            "/api/marketing",
            "/api/blog",
            "/api/auth",
            "/api/settings/public",
        ];

        private readonly RequestDelegate _next;

        public MarketingScopeMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (context.User.IsInRole(nameof(UserRole.Marketing))
                && context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && !AllowedPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Marketing account",
                    Detail = "Marketing accounts can only use the marketing console.",
                }, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
                return;
            }

            if (context.User.IsInRole(nameof(UserRole.Marketing)) && context.Request.Path.StartsWithSegments("/hubs"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await _next(context);
        }
    }
}
