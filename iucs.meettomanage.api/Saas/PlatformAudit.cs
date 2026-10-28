using System.Security.Claims;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Saas;

/// <summary>
/// Writes the platform audit trail. A failure to write is logged and swallowed: an audit outage
/// must not stop an operator from, say, suspending a tenant during an incident.
/// </summary>
public sealed class PlatformAuditWriter(IDbContextFactory<MasterDbContext> factory, ILogger<PlatformAuditWriter> logger)
{
    public async Task WriteAsync(string actor, string action, string method, string path, int statusCode,
        Guid? tenantId, string? ip, string? detail = null)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Actor = Clip(actor, 320), Action = Clip(action, 120), HttpMethod = Clip(method, 8), Path = Clip(path, 400),
                TenantId = tenantId, StatusCode = statusCode, IpAddress = ip is null ? null : Clip(ip, 64),
                Detail = detail is null ? null : Clip(detail, 300),
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write platform audit entry {Action} by {Actor}.", action, actor);
        }
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Records every state-changing request (anything but GET/HEAD/OPTIONS) made with a platform-admin
/// token, together with the HTTP status it ended with. Registered globally; it ignores all other callers.
/// </summary>
public sealed class PlatformAuditFilter(PlatformAuditWriter writer) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var audited = http.User.FindFirstValue("purpose") == "platform-admin"
            && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
            && !HttpMethods.IsOptions(http.Request.Method);
        var executed = await next();
        if (!audited) return;

        // An unhandled exception is turned into a 4xx/5xx later by the error middleware; record it as a failure.
        var failure = executed.Exception;
        var status = failure is AppException app ? app.StatusCode
            : failure is not null ? 500
            : executed.Result is Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult r ? r.StatusCode ?? 200
            : http.Response.StatusCode;
        var route = context.RouteData.Values;
        var path = http.Request.Path.Value ?? "";
        // {id} names a client only on /tenants/{id}/...; elsewhere it is a plan or an invoice.
        var tenantRoute = route.TryGetValue("tenantId", out var t) ? t
            : path.Contains("/tenants/", StringComparison.OrdinalIgnoreCase) && route.TryGetValue("id", out var i) ? i : null;
        Guid? tenantId = Guid.TryParse(tenantRoute?.ToString(), out var g) ? g : null;
        await writer.WriteAsync(http.User.Identity?.Name ?? "unknown",
            $"{route["controller"]}.{route["action"]}", http.Request.Method, path, status,
            tenantId, http.Connection.RemoteIpAddress?.ToString(), failure?.Message);
    }
}
