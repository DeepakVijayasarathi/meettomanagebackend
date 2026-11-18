using System.Globalization;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace iucs.meettomanage.api.Tenancy;

public sealed class TenantDomainResolver(MasterDbContext master, IConfiguration configuration, IMemoryCache cache)
{
    private static string CacheKey(string host) => "tenant-domain:" + host;
    public static string? Normalize(string? value)
    {
        var host = value?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (host.Contains(':') || host.Contains('/') || Uri.CheckHostName(host) != UriHostNameType.Dns)
            throw new DomainValidationException("Enter a hostname only, for example academy.example.com.");
        return new IdnMapping().GetAscii(host).ToLowerInvariant();
    }

    public HashSet<string> ReservedHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1" };
        foreach (var value in new[] { configuration["Frontend:BaseUrl"], configuration["Api:BaseUrl"] })
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) hosts.Add(uri.IdnHost.TrimEnd('.').ToLowerInvariant());
        foreach (var value in configuration.GetSection("MultiTenancy:ReservedHosts").Get<string[]>() ?? [])
            if (Normalize(value) is { } host) hosts.Add(host);
        return hosts;
    }

    public async Task<string?> ResolveHostAsync(string? rawHost, CancellationToken ct)
    {
        var host = rawHost?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return null;
        if (cache.TryGetValue(CacheKey(host), out string? cached) && string.IsNullOrEmpty(cached)) return null;
        var code = await master.Tenants.AsNoTracking()
            .Where(x => x.Status == domain.Entities.Tenancy.TenantStatus.Active
                && x.CustomDomainVerifiedAtUtc != null && x.CustomDomain == host)
            .Select(x => x.Code).SingleOrDefaultAsync(ct);
        // Cache misses briefly, but never cache a positive routing decision. Domain
        // removal/suspension must take effect on every API instance immediately.
        if (code is null) cache.Set(CacheKey(host), "", TimeSpan.FromSeconds(10));
        return code;
    }

    public void Invalidate(string? host) { if (host is not null) cache.Remove(CacheKey(host)); }
}
