using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace iucs.meettomanage.api.Saas;

/// <summary>What a tenant may do right now. A tenant with no subscription row is unmanaged and unrestricted.</summary>
public sealed record TenantEntitlements(
    bool Managed, string? PlanCode, string? PlanName, SaasSubscriptionStatus? Status, IReadOnlyDictionary<string, long> Values)
{
    public static readonly TenantEntitlements Unmanaged = new(false, null, null, null, new Dictionary<string, long>());

    /// <summary>Numeric limit; a plan that does not define the key does not limit it.</summary>
    public long Limit(string key) => !Managed ? EntitlementKeys.Unlimited
        : Values.TryGetValue(key, out var value) ? value : EntitlementKeys.Unlimited;

    /// <summary>Feature flag; a managed plan must grant it explicitly.</summary>
    public bool Can(string feature) => !Managed || (Values.TryGetValue(feature, out var value) && value != 0);
}

public interface IEntitlementService
{
    Task<TenantEntitlements> GetAsync(Guid tenantId, CancellationToken ct = default);
    void Invalidate(Guid tenantId);
    /// <summary>Drops every cached tenant (a plan's entitlements changed).</summary>
    void InvalidateAll();
}

/// <summary>Resolves plan + overrides per tenant and caches the result briefly so requests never hit the master database each time.</summary>
public sealed class EntitlementService(IDbContextFactory<MasterDbContext> factory, IMemoryCache cache) : IEntitlementService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private int _generation;

    private string Key(Guid tenantId) => $"saas-ent:{_generation}:{tenantId:N}";

    public async Task<TenantEntitlements> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key(tenantId), out TenantEntitlements? cached) && cached is not null) return cached;
        await using var db = await factory.CreateDbContextAsync(ct);
        var sub = await db.TenantSubscriptions.AsNoTracking().Include(x => x.Plan).ThenInclude(x => x.Entitlements)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        TenantEntitlements result;
        if (sub is null) result = TenantEntitlements.Unmanaged;
        else
        {
            var overrides = await db.TenantEntitlementOverrides.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(ct);
            result = new TenantEntitlements(true, sub.Plan.Code, sub.Plan.Name, sub.Status,
                SaasRules.ResolveEntitlements(sub.Plan.Entitlements, overrides, DateTime.UtcNow));
        }
        cache.Set(Key(tenantId), result, Ttl);
        return result;
    }

    public void Invalidate(Guid tenantId) => cache.Remove(Key(tenantId));

    public void InvalidateAll() => Interlocked.Increment(ref _generation);
}
