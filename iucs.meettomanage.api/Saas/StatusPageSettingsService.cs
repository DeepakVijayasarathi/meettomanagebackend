using System.Text.Json;
using iucs.meettomanage.application.Common.Options;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace iucs.meettomanage.api.Saas;

public sealed class StatusPageSettingsService(IDbContextFactory<MasterDbContext> factory, IMemoryCache cache)
{
    private const string Prefix = "statusPage.";
    private const string CacheKey = "platform-status-page-settings";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static StatusPageOptions Defaults() => new()
    {
        Enabled = true, Title = "Meet to Manage service status",
        Description = "Live availability of the Meet to Manage platform and classroom services.",
        SupportUrl = "/contact", RefreshSeconds = 30,
        Components = [
            new() { Key = "website-api", Name = "Website and API", Type = "Api" },
            new() { Key = "platform-data", Name = "Platform data", Type = "Database" },
            new() { Key = "live-classrooms", Name = "Live classrooms", Type = "Server", Server = "Jitsi / Video" },
            new() { Key = "class-recordings", Name = "Class recordings", Type = "Server", Server = "Recording Worker" },
        ],
    };

    public async Task<StatusPageOptions> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out StatusPageOptions? hit) && hit is not null) return hit;
        await using var db = await factory.CreateDbContextAsync(ct);
        var saved = await db.PlatformSettings.AsNoTracking().Where(x => x.Key.StartsWith(Prefix)).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        var value = Defaults();
        string? S(string key) => saved.GetValueOrDefault(Prefix + key);
        value.Enabled = bool.TryParse(S("enabled"), out var enabled) ? enabled : value.Enabled;
        value.Title = S("title") ?? value.Title; value.Description = S("description") ?? value.Description;
        value.SupportUrl = S("supportUrl") ?? value.SupportUrl;
        value.RefreshSeconds = int.TryParse(S("refreshSeconds"), out var seconds) ? Math.Clamp(seconds, 15, 300) : value.RefreshSeconds;
        value.ManualStatus = S("manualStatus") ?? "Operational"; value.IncidentTitle = S("incidentTitle"); value.IncidentMessage = S("incidentMessage");
        value.IncidentStartedAtUtc = DateTime.TryParse(S("incidentStartedAtUtc"), out var started) ? started.ToUniversalTime() : null;
        if (S("components") is { Length: > 0 } components)
            try { value.Components = JsonSerializer.Deserialize<List<StatusPageComponentOptions>>(components, Json) ?? value.Components; } catch (JsonException) { }
        cache.Set(CacheKey, value, TimeSpan.FromSeconds(30));
        return value;
    }

    public async Task<string?> SaveAsync(StatusPageOptions value, CancellationToken ct = default)
    {
        value.Title = value.Title?.Trim() ?? ""; value.Description = value.Description?.Trim() ?? ""; value.SupportUrl = value.SupportUrl?.Trim();
        value.RefreshSeconds = Math.Clamp(value.RefreshSeconds, 15, 300);
        if (value.Title.Length is < 2 or > 120) return "Status page title must be 2 to 120 characters.";
        if (value.Description.Length > 500) return "Description cannot exceed 500 characters.";
        if (value.SupportUrl?.Length > 500 || !SafeSupportUrl(value.SupportUrl)) return "Support link must be a relative path or an http/https address.";
        if (value.IncidentTitle?.Length > 160 || value.IncidentMessage?.Length > 1500) return "Incident title or message is too long.";
        if (value.Components.Count is < 1 or > 30) return "Add between 1 and 30 service components.";
        string[] types = ["Api", "Database", "Server", "Service"], statuses = ["Operational", "Degraded", "Maintenance", "MajorOutage"];
        if (!IsValidStatus(value.ManualStatus, statuses)) return "Choose a valid overall status.";
        foreach (var component in value.Components)
        {
            component.Key = component.Key?.Trim() ?? ""; component.Name = component.Name?.Trim() ?? "";
            if (component.Key.Length is < 1 or > 60 || component.Name.Length is < 1 or > 100) return "Every component needs a valid key and name.";
            if (component.Description?.Length > 300 || component.Server?.Length > 120 || component.Service?.Length > 120) return $"{component.Name} contains a value that is too long.";
            if (!types.Contains(component.Type, StringComparer.OrdinalIgnoreCase)) return $"{component.Name} has an invalid check type.";
            if (!IsValidStatus(component.ManualStatus, statuses)) return $"{component.Name} has an invalid status override.";
        }
        if (value.Components.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Components.Count) return "Component keys must be unique.";
        var components = JsonSerializer.Serialize(value.Components, Json);
        if (components.Length > 2000) return "The component configuration is too large; shorten descriptions or use fewer components.";
        var values = new Dictionary<string, string?> {
            ["enabled"] = value.Enabled.ToString(), ["title"] = value.Title, ["description"] = value.Description, ["supportUrl"] = value.SupportUrl,
            ["refreshSeconds"] = value.RefreshSeconds.ToString(), ["manualStatus"] = NormalizeStatus(value.ManualStatus),
            ["incidentTitle"] = Clean(value.IncidentTitle), ["incidentMessage"] = Clean(value.IncidentMessage),
            ["incidentStartedAtUtc"] = value.IncidentStartedAtUtc?.ToUniversalTime().ToString("O"), ["components"] = components,
        };
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.PlatformSettings.Where(x => x.Key.StartsWith(Prefix)).ToDictionaryAsync(x => x.Key, ct);
        foreach (var (suffix, content) in values) {
            var key = Prefix + suffix;
            if (!existing.TryGetValue(key, out var row)) db.PlatformSettings.Add(row = new PlatformSetting { Key = key });
            row.Value = content; row.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct); cache.Remove(CacheKey); return null;
    }

    private static string NormalizeStatus(string? value) => value?.Trim().ToLowerInvariant() switch { "degraded" => "Degraded", "maintenance" => "Maintenance", "majoroutage" or "major-outage" => "MajorOutage", _ => "Operational" };
    private static bool IsValidStatus(string? value, IEnumerable<string> allowed) => string.IsNullOrWhiteSpace(value) || allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool SafeSupportUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/')) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
