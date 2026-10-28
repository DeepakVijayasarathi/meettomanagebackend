using System.ComponentModel.DataAnnotations;

namespace iucs.meettomanage.domain.Entities.Saas;

/// <summary>
/// Append-only record of one operator action on the platform console (a state change or a
/// sign-in attempt). Request bodies are never stored, so secrets and PINs cannot leak into it.
/// </summary>
public class PlatformAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>The operator's sign-in email (or the email that was tried, for a failed sign-in).</summary>
    [MaxLength(320)] public string Actor { get; set; } = "";
    /// <summary>Stable action name, e.g. <c>Platform.SetStatus</c> or <c>Platform.LoginFailed</c>.</summary>
    [MaxLength(120)] public string Action { get; set; } = "";
    [MaxLength(8)] public string HttpMethod { get; set; } = "";
    /// <summary>Request path, without the query string.</summary>
    [MaxLength(400)] public string Path { get; set; } = "";
    /// <summary>The client the action targeted, when the route names one.</summary>
    public Guid? TenantId { get; set; }
    public int StatusCode { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
    [MaxLength(300)] public string? Detail { get; set; }
}
