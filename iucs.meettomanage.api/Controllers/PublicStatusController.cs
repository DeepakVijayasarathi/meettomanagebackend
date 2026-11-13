using iucs.meettomanage.application.Common.Options;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using iucs.meettomanage.api.Saas;

namespace iucs.meettomanage.api.Controllers;

[ApiController, Route("api/status")]
public sealed class PublicStatusController(IMonitoringService monitoring, MasterDbContext master, StatusPageSettingsService settings) : ControllerBase
{
    [HttpGet]
    [ResponseCache(Duration = 15, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var options = await settings.GetAsync(ct);
        if (!options.Enabled) return NotFound();
        var infrastructureTask = monitoring.GetPlatformInfrastructureAsync(ct);
        bool databaseHealthy;
        try { databaseHealthy = await master.Database.CanConnectAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { databaseHealthy = false; }
        var infrastructure = await infrastructureTask;
        var definitions = options.Components.Count > 0 ? options.Components : DefaultComponents(infrastructure.Servers.Select(x => x.Name));
        var components = definitions.Select(c =>
        {
            var status = Normalise(c.ManualStatus);
            if (status == "Operational") status = AutomaticStatus(c, databaseHealthy, infrastructure);
            return new { key = SafeKey(c.Key), name = c.Name.Trim(), description = c.Description?.Trim(), status };
        }).ToList();
        var manual = Normalise(options.ManualStatus);
        var overall = manual != "Operational" ? manual
            : components.Any(x => x.status == "MajorOutage") ? "MajorOutage"
            : components.Any(x => x.status == "Degraded") ? "Degraded"
            : components.Any(x => x.status == "Maintenance") ? "Maintenance" : "Operational";
        var hasIncident = overall != "Operational" || !string.IsNullOrWhiteSpace(options.IncidentTitle);
        return Ok(new
        {
            title = options.Title.Trim(), description = options.Description.Trim(), supportUrl = options.SupportUrl,
            refreshSeconds = Math.Clamp(options.RefreshSeconds, 15, 300), overallStatus = overall, components,
            incident = hasIncident ? new { title = options.IncidentTitle ?? StatusLabel(overall), message = options.IncidentMessage, startedAtUtc = options.IncidentStartedAtUtc } : null,
            checkedAtUtc = DateTime.UtcNow,
        });
    }

    private static List<StatusPageComponentOptions> DefaultComponents(IEnumerable<string> servers) =>
        [new() { Key = "api", Name = "Website and API", Type = "Api" }, new() { Key = "database", Name = "Platform database", Type = "Database" },
         .. servers.Select((name, i) => new StatusPageComponentOptions { Key = $"server-{i + 1}", Name = name, Type = "Server", Server = name })];

    private static string AutomaticStatus(StatusPageComponentOptions component, bool databaseHealthy, iucs.meettomanage.application.Dto.Monitoring.PlatformInfrastructureDto infrastructure)
    {
        if (component.Type.Equals("Api", StringComparison.OrdinalIgnoreCase)) return "Operational";
        if (component.Type.Equals("Database", StringComparison.OrdinalIgnoreCase)) return databaseHealthy ? "Operational" : "MajorOutage";
        var server = infrastructure.Servers.FirstOrDefault(x => x.Name.Equals(component.Server, StringComparison.OrdinalIgnoreCase));
        if (server is null) return "Degraded";
        if (!server.Reachable) return server.IsOnDemand ? "Operational" : "MajorOutage";
        if (component.Type.Equals("Service", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(component.Service))
            return server.Services.Any(x => x.Name.Equals(component.Service, StringComparison.OrdinalIgnoreCase) && x.Active) ? "Operational" : "MajorOutage";
        return server.CpuUsagePercent >= 95 || server.MemoryUsedPercent >= 95 || server.DiskUsedPercent >= 95 ? "Degraded" : "Operational";
    }
    private static string Normalise(string? value) => value?.Trim().ToLowerInvariant() switch { "degraded" => "Degraded", "maintenance" => "Maintenance", "majoroutage" or "major_outage" or "outage" => "MajorOutage", _ => "Operational" };
    private static string StatusLabel(string value) => value switch { "MajorOutage" => "Service outage", "Degraded" => "Degraded performance", "Maintenance" => "Scheduled maintenance", _ => "Operational" };
    private static string SafeKey(string value) => string.Concat((value ?? "component").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')).Trim('-');
}
