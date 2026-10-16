using iucs.meettomanage.application.Common;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Tenancy;

/// <summary>
/// Starts one existing worker per active tenant, supplying a scope factory that
/// binds every scope to that tenant. New tenants are picked up without redeployment.
/// Suspended tenants' workers are cancelled; one tenant failure does not stop others.
/// </summary>
public sealed class TenantHostedService<T>(IServiceProvider services,
    IDbContextFactory<MasterDbContext> masterFactory, TenantScopeFactory scopes,
    ILogger<TenantHostedService<T>> logger) : BackgroundService where T : BackgroundService
{
    private readonly Dictionary<Guid, T> _workers = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var master = await masterFactory.CreateDbContextAsync(stoppingToken);
                    var active = await master.Tenants.AsNoTracking().Where(x => x.Status == TenantStatus.Active).ToListAsync(stoppingToken);
                    foreach (var id in _workers.Keys.Except(active.Select(x => x.Id)).ToList()) await StopAsync(id);
                    foreach (var tenant in active)
                    {
                        if (_workers.TryGetValue(tenant.Id, out var existing))
                        {
                            if (existing.ExecuteTask?.IsCompleted != true) continue;
                            await StopAsync(tenant.Id);
                        }
                        T? worker = null;
                        try
                        {
                            using var ambient = TenantExecution.Enter(tenant.Id, tenant.Code);
                            worker = ActivatorUtilities.CreateInstance<T>(services, new BoundScopes(scopes, tenant));
                            await worker.StartAsync(stoppingToken);
                            _workers.Add(tenant.Id, worker);
                        }
                        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                        {
                            worker?.Dispose();
                            logger.LogError(ex, "Could not start {Worker} for tenant {TenantId}; other tenants continue.", typeof(T).Name, tenant.Id);
                        }
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Tenant worker discovery failed for {Worker}; retrying.", typeof(T).Name);
                }
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
        finally
        {
            foreach (var id in _workers.Keys.ToList()) await StopAsync(id);
        }
    }

    private async Task StopAsync(Guid id)
    {
        var worker = _workers[id];
        _workers.Remove(id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await worker.StopAsync(timeout.Token); }
        catch (OperationCanceledException) { }
        finally { worker.Dispose(); }
    }

    private sealed class BoundScopes(TenantScopeFactory factory, Tenant tenant) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => factory.CreateScope(tenant);
    }
}
