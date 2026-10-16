using iucs.meettomanage.api.Data;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Tenancy;

public static class TenantServices
{
    public static T InTenant<T>(IServiceProvider services, Func<T> factory)
    {
        var tenant = BoundTenant(services);
        using var ambient = TenantExecution.Enter(tenant.TenantId ?? throw new InvalidOperationException("A tenant is required."), tenant.TenantCode!);
        return factory();
    }
    // SignalR creates a fresh DI scope per invocation. Rebind only from the
    // principal already validated by JwtBearer, never from an unverified token.
    public static TenantContext BoundTenant(IServiceProvider services)
    {
        var tenant = services.GetRequiredService<TenantContext>();
        if (tenant.TenantId is not null) return tenant;
        var user = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true
            && Guid.TryParse(user.FindFirst(TenantContext.TenantIdClaim)?.Value, out var id)
            && user.FindFirst(TenantContext.TenantCodeClaim)?.Value is { } code)
            services.GetRequiredService<TenantResolver>().BindKnownTokenAsync(id, code).GetAwaiter().GetResult();
        return tenant;
    }
    public static void AddTenantServices(this IServiceCollection services, IConfiguration configuration)
    {
        foreach (var key in new[] { "MasterDb", "TenantServer" })
            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(key)))
                throw new InvalidOperationException($"ConnectionStrings:{key} is required when MultiTenancy:Enabled is true.");
        services.AddDbContextFactory<MasterDbContext>(o => o.UseNpgsql(configuration.GetConnectionString("MasterDb")));
        services.AddScoped<TenantContext>();
        services.AddScoped<TenantResolver>();
        services.AddScoped<LoginDirectory>();
        services.AddScoped<LoginDirectoryInterceptor>();
        services.AddScoped<TenantProvisioner>();
        services.AddSingleton<TenantScopeFactory>();
        services.AddHostedService<TenantHostedService<DirectoryBackgroundService>>();
    }

    public static void AddTenantWorker<T>(this IServiceCollection services, bool enabled) where T : BackgroundService
    {
        if (enabled) services.AddHostedService<TenantHostedService<T>>();
        else services.AddHostedService<T>();
    }

    public static async Task InitializeTenantsAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup")) await master.Database.MigrateAsync();
        var tenants = await master.Tenants.AsNoTracking()
            .Where(x => x.Status == domain.Entities.Tenancy.TenantStatus.Active).ToListAsync();
        var seed = new ConfigurationBuilder().AddConfiguration(app.Configuration).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:AdminEmail"] = "", ["Seed:AdminPin"] = "",
        }).Build();
        foreach (var tenant in tenants)
        {
            try
            {
                using var ambient = TenantExecution.Enter(tenant.Id, tenant.Code);
                // Never converge a tenant's PIN back to a global seed on restart.
                await DatabaseInitializer.InitializeAsync(app.Services, seed, tenant);
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Startup initialization failed for tenant {TenantId}; other tenants remain available.", tenant.Id);
            }
        }
    }
}
