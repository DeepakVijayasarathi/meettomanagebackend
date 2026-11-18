using System.Globalization;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Saas;

public sealed record AiAllowance(string Month, long UsedTokens, long Limit)
{
    public bool Unlimited => Limit == EntitlementKeys.Unlimited;
    public bool Exhausted => !Unlimited && UsedTokens >= Limit;
    public long? Remaining => Unlimited ? null : Math.Max(0, Limit - UsedTokens);
}

public sealed record AiFeatureUsage(string Feature, long Requests, long PromptTokens, long CompletionTokens)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// Counts tokens spent on the platform's AI key per tenant, month and feature in the master database, and enforces
/// each academy's monthly plan allowance (<see cref="EntitlementKeys.MaxAiTokensPerMonth"/>). The marketing site is
/// counted under <see cref="AiTokenUsage.MarketingTenantId"/> and never limited or charged to an academy.
/// </summary>
public sealed class AiTokenMeter(IDbContextFactory<MasterDbContext> factory, IEntitlementService entitlements, ILogger<AiTokenMeter> logger)
{
    public static string MonthOf(DateTime utc) => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static bool IsValidMonth(string? month) =>
        month is { Length: 7 } && DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public async Task<AiAllowance> AllowanceAsync(Guid tenantId, CancellationToken ct = default)
    {
        var month = MonthOf(DateTime.UtcNow);
        var limit = (await entitlements.GetAsync(tenantId, ct)).Limit(EntitlementKeys.MaxAiTokensPerMonth);
        await using var db = await factory.CreateDbContextAsync(ct);
        var used = await db.AiTokenUsage.AsNoTracking().Where(x => x.TenantId == tenantId && x.Month == month)
            .SumAsync(x => x.PromptTokens + x.CompletionTokens, ct);
        return new AiAllowance(month, used, limit);
    }

    /// <summary>Refuses the call once this month's allowance is used up. A reply may run a little past the limit.</summary>
    public async Task EnsureWithinAllowanceAsync(Guid tenantId, CancellationToken ct = default)
    {
        var allowance = await AllowanceAsync(tenantId, ct);
        if (allowance.Exhausted)
            throw new PlanLimitException(allowance.Limit == 0
                ? "AI features aren't included in your academy's plan. Upgrade to use them."
                : $"Your academy has used this month's AI allowance ({allowance.Limit.ToString("N0", CultureInfo.InvariantCulture)} tokens). It resets on the 1st, or upgrade your plan for more.");
    }

    /// <summary>Adds one call's tokens. Never throws: metering must not lose an answer that was already paid for.</summary>
    public async Task RecordAsync(Guid tenantId, string feature, long promptTokens, long completionTokens, CancellationToken ct = default)
    {
        var month = MonthOf(DateTime.UtcNow);
        feature = string.IsNullOrWhiteSpace(feature) ? "other" : feature.Trim()[..Math.Min(feature.Trim().Length, 40)];
        promptTokens = Math.Max(0, promptTokens);
        completionTokens = Math.Max(0, completionTokens);
        try
        {
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            // Increment in place so concurrent calls (several API instances) never lose a count; insert the row the
            // first time, and if another call inserted it first, the retry increments that row.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var now = DateTime.UtcNow;
                var updated = await db.AiTokenUsage.Where(x => x.TenantId == tenantId && x.Month == month && x.Feature == feature)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Requests, x => x.Requests + 1)
                        .SetProperty(x => x.PromptTokens, x => x.PromptTokens + promptTokens)
                        .SetProperty(x => x.CompletionTokens, x => x.CompletionTokens + completionTokens)
                        .SetProperty(x => x.UpdatedAtUtc, now), CancellationToken.None);
                if (updated > 0) return;
                var row = new AiTokenUsage
                {
                    TenantId = tenantId, Month = month, Feature = feature, Requests = 1,
                    PromptTokens = promptTokens, CompletionTokens = completionTokens, UpdatedAtUtc = now,
                };
                db.AiTokenUsage.Add(row);
                try { await db.SaveChangesAsync(CancellationToken.None); return; }
                catch (DbUpdateException) { db.Entry(row).State = EntityState.Detached; }
            }
            logger.LogWarning("AI usage for tenant {TenantId} could not be recorded after retries.", tenantId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI usage for tenant {TenantId} ({Feature}) could not be recorded.", tenantId, feature);
        }
    }

    public async Task<IReadOnlyList<AiFeatureUsage>> FeaturesAsync(Guid tenantId, string month, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AiTokenUsage.AsNoTracking().Where(x => x.TenantId == tenantId && x.Month == month)
            .OrderBy(x => x.Feature)
            .Select(x => new AiFeatureUsage(x.Feature, x.Requests, x.PromptTokens, x.CompletionTokens)).ToListAsync(ct);
    }

    /// <summary>Every counter for one month, for the platform console.</summary>
    public async Task<IReadOnlyList<AiTokenUsage>> MonthAsync(string month, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AiTokenUsage.AsNoTracking().Where(x => x.Month == month).ToListAsync(ct);
    }
}
