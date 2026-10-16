using System.Text.Json;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;

namespace iucs.meettomanage.api.Tenancy;

/// <summary>Runs after authentication/rate limiting and before controller construction.</summary>
public sealed class TenantRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, TenantResolver resolver, TenantContext tenant)
    {
        var path = http.Request.Path.Value ?? "";
        // These legacy APIs use server-wide SSH credentials and backup folders,
        // not the scoped business database. Never expose other clients' dumps,
        // logs, infrastructure controls or monitoring streams to a tenant Admin.
        if ((path.StartsWith("/api/monitoring", StringComparison.OrdinalIgnoreCase)
             && path is not ("/api/monitoring/live-users" or "/api/monitoring/today-sessions" or "/api/monitoring/broadcast-reload"))
            || path.StartsWith("/hubs/monitoring", StringComparison.OrdinalIgnoreCase))
            throw new ForbiddenException("Shared infrastructure monitoring is reserved for the service operator.");
        var anonymousLogin = path is "/api/auth/login" or "/api/auth/forgot-pin";
        if (anonymousLogin)
        {
            // Resolve before MVC constructs AuthService and its tenant UnitOfWork.
            http.Request.EnableBuffering();
            try
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                if (body.RootElement.ValueKind != JsonValueKind.Object)
                    throw new DomainValidationException("The login request is invalid.");
                var email = body.RootElement.EnumerateObject()
                    .FirstOrDefault(x => x.Name.Equals("email", StringComparison.OrdinalIgnoreCase)).Value;
                if (email.ValueKind != JsonValueKind.String)
                    throw new DomainValidationException("Enter an email or mobile number.");
                await resolver.ResolveLoginAsync(email.GetString()!, http.RequestAborted);
                if (http.Items["TenantPathCode"] is string loginCode && loginCode != tenant.TenantCode)
                    throw new UnauthorizedException("Invalid email/mobile number or PIN.");
            }
            catch (JsonException) { throw new DomainValidationException("The login request is invalid."); }
            finally { http.Request.Body.Position = 0; }
        }
        else if (http.Items["TenantPathCode"] is string code)
        {
            if (tenant.TenantId is null) await resolver.ResolveCodeAsync(code, http.RequestAborted);
            else if (tenant.TenantCode != code) throw new ForbiddenException("The URL does not belong to this tenant.");
        }

        if (tenant.TenantId is { } id)
        {
            using var ambient = TenantExecution.Enter(id, tenant.TenantCode!);
            await next(http);
        }
        else if (path.StartsWith("/api/platform", StringComparison.Ordinal) || path == "/health"
                 || path.StartsWith("/openapi", StringComparison.Ordinal) || path.StartsWith("/scalar", StringComparison.Ordinal))
            await next(http);
        else if (http.Request.Method == "OPTIONS") await next(http);
        else if (path == "/api/settings/public") await http.Response.WriteAsJsonAsync(Array.Empty<object>());
        else if (http.User.Identity?.IsAuthenticated == true)
            throw new ForbiddenException("A tenant-bound session is required.");
        else
            throw new DomainValidationException("Open this page using the client's /t/{tenantCode} URL.");
    }
}
