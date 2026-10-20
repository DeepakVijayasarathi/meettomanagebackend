using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Saas;

public static class SaasServices
{
    /// <summary>Registers the platform billing layer. Only called in multi-tenant mode (it needs the master database).</summary>
    public static IServiceCollection AddSaasServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SaasOptions>(configuration.GetSection(SaasOptions.Section));
        services.AddMemoryCache();
        services.AddHttpClient(nameof(RazorpayPlatformGateway));
        services.AddSingleton<IEntitlementService, EntitlementService>();
        services.AddSingleton<PlatformEmailSender>();
        services.AddSingleton<IPlatformPaymentGateway>(sp => string.Equals(sp.GetRequiredService<IOptions<SaasOptions>>().Value.Gateway, "Razorpay", StringComparison.OrdinalIgnoreCase)
            ? ActivatorUtilities.CreateInstance<RazorpayPlatformGateway>(sp)
            : new ManualPlatformGateway());
        services.AddSingleton<SaasUsageService>();
        services.AddSingleton<SaasReminderService>();
        services.AddSingleton<SaasSubscriptionService>();
        services.AddScoped<EntitlementInterceptor>();
        services.AddSingleton<SaasLifecycleBackgroundService>();
        services.AddSingleton<SaasUsageBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<SaasLifecycleBackgroundService>());
        services.AddHostedService(sp => sp.GetRequiredService<SaasUsageBackgroundService>());
        return services;
    }

    /// <summary>Seeds plans, templates and legacy subscriptions. Failure is logged, never fatal: billing is optional at start-up.</summary>
    public static async Task SeedSaasAsync(this IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SaasSeeder");
        try
        {
            await SaasSeeder.SeedAsync(services.GetRequiredService<IDbContextFactory<MasterDbContext>>(),
                services.GetRequiredService<IOptions<SaasOptions>>(), logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SaaS seeding failed. Apply the master-database migrations (AddSaasBilling) and restart.");
        }
    }
}
