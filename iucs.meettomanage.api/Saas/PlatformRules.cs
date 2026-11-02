using System.Globalization;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Saas;

/// <summary>
/// Operator-editable platform rules (Platform console → Rules). Saved in platform_setting; anything never saved
/// falls back to the server's configuration, then to the built-in default.
/// </summary>
public sealed record PlatformRules
{
    /// <summary>Overview flags a free/trial academy with no students or classes this many days after signup.</summary>
    public int NotStartedAfterDays { get; init; } = 3;
    /// <summary>Overview flags trials ending within this many days.</summary>
    public int TrialEndingWindowDays { get; init; } = 7;
    /// <summary>Academies see an upgrade banner, and get a warning e-mail, at this share of a plan limit.</summary>
    public int UsageWarningPercent { get; init; } = 80;
    /// <summary>Plan an unpaid trial moves to when it ends; empty pauses the workspace instead.</summary>
    public string? TrialFallbackPlanCode { get; init; } = "free";

    public string? Validate()
    {
        if (NotStartedAfterDays is < 1 or > 60) return "\"Hasn't started\" must be 1 to 60 days.";
        if (TrialEndingWindowDays is < 1 or > 30) return "The trial-ending warning must be 1 to 30 days ahead.";
        if (UsageWarningPercent is < 50 or > 99) return "The usage warning must be between 50% and 99%.";
        return null;
    }
}

public sealed class PlatformRulesService(IDbContextFactory<MasterDbContext> factory, IOptions<SaasOptions> options, IMemoryCache cache)
{
    private const string CacheKey = "platform-rules";
    private const string P = "rules.";

    private PlatformRules Defaults() => new()
    {
        TrialFallbackPlanCode = string.IsNullOrWhiteSpace(options.Value.TrialFallbackPlanCode) ? null : options.Value.TrialFallbackPlanCode.Trim().ToLowerInvariant(),
    };

    public async Task<PlatformRules> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out PlatformRules? cached) && cached is not null) return cached;
        await using var db = await factory.CreateDbContextAsync(ct);
        var saved = await db.PlatformSettings.AsNoTracking().Where(x => x.Key.StartsWith(P)).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        var d = Defaults();
        int I(string key, int fallback, int min, int max) =>
            saved.TryGetValue(P + key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n : fallback;
        var rules = d with
        {
            NotStartedAfterDays = I("notStartedAfterDays", d.NotStartedAfterDays, 1, 60),
            TrialEndingWindowDays = I("trialEndingWindowDays", d.TrialEndingWindowDays, 1, 30),
            UsageWarningPercent = I("usageWarningPercent", d.UsageWarningPercent, 50, 99),
            // A saved empty value means "pause the workspace": it deliberately overrides the server default.
            TrialFallbackPlanCode = saved.TryGetValue(P + "trialFallbackPlanCode", out var code) ? (string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToLowerInvariant()) : d.TrialFallbackPlanCode,
        };
        cache.Set(CacheKey, rules, TimeSpan.FromMinutes(1));
        return rules;
    }

    /// <summary>Saves the rules. Returns the first problem (including a fallback plan that isn't an active free plan), or null.</summary>
    public async Task<string?> SaveAsync(PlatformRules rules, CancellationToken ct = default)
    {
        if (rules.Validate() is { } problem) return problem;
        await using var db = await factory.CreateDbContextAsync(ct);
        var code = string.IsNullOrWhiteSpace(rules.TrialFallbackPlanCode) ? null : rules.TrialFallbackPlanCode.Trim().ToLowerInvariant();
        if (code is not null)
        {
            var plans = await db.Plans.Include(x => x.Prices).Where(x => x.Code == code).ToListAsync(ct);
            if (SaasRules.TrialFallback(plans, code) is null) return $"\"{code}\" isn't an active plan that costs nothing, so trials can't move to it.";
        }
        var values = new Dictionary<string, string>
        {
            [P + "notStartedAfterDays"] = rules.NotStartedAfterDays.ToString(CultureInfo.InvariantCulture),
            [P + "trialEndingWindowDays"] = rules.TrialEndingWindowDays.ToString(CultureInfo.InvariantCulture),
            [P + "usageWarningPercent"] = rules.UsageWarningPercent.ToString(CultureInfo.InvariantCulture),
            [P + "trialFallbackPlanCode"] = code ?? "",
        };
        var existing = await db.PlatformSettings.Where(x => x.Key.StartsWith(P)).ToDictionaryAsync(x => x.Key, ct);
        foreach (var (key, value) in values)
        {
            if (!existing.TryGetValue(key, out var row)) db.PlatformSettings.Add(row = new PlatformSetting { Key = key });
            row.Value = value;
            row.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
        return null;
    }
}
