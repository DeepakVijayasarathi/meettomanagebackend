using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace iucs.meettomanage.api.Controllers;

[ApiController, Route("api/client-errors"), AllowAnonymous]
public sealed class ClientErrorsController(ILogger<ClientErrorsController> logger) : ControllerBase
{
    public sealed class Report
    {
        [Required, StringLength(300)] public string Message { get; set; } = "";
        [StringLength(500)] public string? Path { get; set; }
        [StringLength(3000)] public string? Stack { get; set; }
        [StringLength(80)] public string? Release { get; set; }
        [StringLength(40)] public string? Kind { get; set; }
    }

    [HttpPost, EnableRateLimiting("login")]
    public IActionResult Create(Report report)
    {
        logger.LogWarning("Browser failure {Kind} at {Path}: {Message}\n{ClientStack}",
            report.Kind ?? "unknown", report.Path ?? "unknown", report.Message, report.Stack ?? "");
        return Accepted(new { correlationId = HttpContext.Items[Middleware.CorrelationIdMiddleware.Item] });
    }
}
