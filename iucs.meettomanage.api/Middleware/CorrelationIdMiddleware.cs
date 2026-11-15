using System.Diagnostics;

namespace iucs.meettomanage.api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string Header = "X-Correlation-ID";
    public const string Item = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[Header].FirstOrDefault();
        var id = supplied is { Length: > 0 and <= 128 } && supplied.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            ? supplied
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        context.Items[Item] = id;
        context.Response.Headers[Header] = id;
        using (logger.BeginScope(new Dictionary<string, object> { [Item] = id }))
            await next(context);
    }
}
