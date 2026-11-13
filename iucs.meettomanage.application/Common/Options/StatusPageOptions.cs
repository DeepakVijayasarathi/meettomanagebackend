namespace iucs.meettomanage.application.Common.Options;

/// <summary>Public, sanitised service-status page. Every component and manual override is deployment configuration.</summary>
public sealed class StatusPageOptions
{
    public bool Enabled { get; set; } = true;
    public string Title { get; set; } = "Meet to Manage status";
    public string Description { get; set; } = "Current availability of the Meet to Manage platform.";
    public string? SupportUrl { get; set; } = "/get-started";
    public int RefreshSeconds { get; set; } = 30;
    /// <summary>Operational, Degraded, Maintenance or MajorOutage. Non-operational values override automatic health.</summary>
    public string ManualStatus { get; set; } = "Operational";
    public string? IncidentTitle { get; set; }
    public string? IncidentMessage { get; set; }
    public DateTime? IncidentStartedAtUtc { get; set; }
    public List<StatusPageComponentOptions> Components { get; set; } = [];
}

public sealed class StatusPageComponentOptions
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Api, Database, Server or Service.</summary>
    public string Type { get; set; } = "Api";
    public string? Server { get; set; }
    public string? Service { get; set; }
    public string? Description { get; set; }
    public string? ManualStatus { get; set; }
}
