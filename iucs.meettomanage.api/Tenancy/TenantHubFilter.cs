using iucs.meettomanage.application.Common;
using Microsoft.AspNetCore.SignalR;

namespace iucs.meettomanage.api.Tenancy;

public sealed class TenantHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var tenant = TenantServices.BoundTenant(context.ServiceProvider);
        // Check active status again on each long-lived websocket operation.
        await context.ServiceProvider.GetRequiredService<TenantResolver>()
            .ResolveTokenAsync(tenant.TenantId!.Value, tenant.TenantCode!, context.Context.ConnectionAborted);
        using var ambient = TenantExecution.Enter(tenant.TenantId.Value, tenant.TenantCode!);
        return await next(context);
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var tenant = TenantServices.BoundTenant(context.ServiceProvider);
        using var ambient = TenantExecution.Enter(tenant.TenantId!.Value, tenant.TenantCode!);
        await next(context);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        var tenant = TenantServices.BoundTenant(context.ServiceProvider);
        using var ambient = TenantExecution.Enter(tenant.TenantId!.Value, tenant.TenantCode!);
        await next(context, exception);
    }
}
