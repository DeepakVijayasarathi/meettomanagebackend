using System.Security.Claims;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.api.Services;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace iucs.meettomanage.api.Controllers;

/// <summary>
/// Course-completion certificates: issued automatically when a batch completes (or by staff), downloadable by
/// the family as a PDF, and verifiable by anyone through the code in the QR.
/// </summary>
[ApiController]
[Route("api/certificates")]
public class CertificatesController(ICertificateService certificates, ISettingsService settings, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    [HasPermission(PermissionModule.CourseBatchManagement, PermissionAction.View)]
    public async Task<ActionResult<IReadOnlyList<CertificateDto>>> List([FromQuery] Guid? batchId, CancellationToken cancellationToken) =>
        Ok(await certificates.ListAsync(batchId, cancellationToken));

    [HttpPost]
    [HasPermission(PermissionModule.CourseBatchManagement, PermissionAction.Edit)]
    public async Task<ActionResult<CertificateDto>> Issue(IssueCertificateRequest request, CancellationToken cancellationToken) =>
        Ok(await certificates.IssueAsync(request, cancellationToken));

    /// <summary>Issues any certificates still owed for completed batches now, instead of waiting for the hourly run.</summary>
    [HttpPost("issue-due")]
    [HasPermission(PermissionModule.CourseBatchManagement, PermissionAction.Edit)]
    public async Task<ActionResult<object>> IssueDue(CancellationToken cancellationToken) =>
        Ok(new { issued = await certificates.IssueForCompletedBatchesAsync(notify: true, cancellationToken) });

    [HttpPost("{id:guid}/revoke")]
    [HasPermission(PermissionModule.CourseBatchManagement, PermissionAction.Edit)]
    public async Task<ActionResult<CertificateDto>> Revoke(Guid id, RevokeCertificateRequest request, CancellationToken cancellationToken) =>
        Ok(await certificates.RevokeAsync(id, request.Reason, cancellationToken));

    [HttpGet("{id:guid}/pdf")]
    [HasPermission(PermissionModule.CourseBatchManagement, PermissionAction.View)]
    public async Task<IActionResult> Pdf(Guid id, CancellationToken cancellationToken) =>
        await PdfAsync(await certificates.GetAsync(id, cancellationToken), cancellationToken);

    [HttpGet("mine")]
    [Authorize(Roles = nameof(UserRole.Parent))]
    public async Task<ActionResult<IReadOnlyList<CertificateDto>>> Mine(CancellationToken cancellationToken) =>
        Ok(await certificates.ListForParentAsync(UserId(), cancellationToken));

    [HttpGet("mine/{id:guid}/pdf")]
    [Authorize(Roles = nameof(UserRole.Parent))]
    public async Task<IActionResult> MyPdf(Guid id, CancellationToken cancellationToken) =>
        await PdfAsync(await certificates.GetForParentAsync(UserId(), id, cancellationToken), cancellationToken);

    /// <summary>Public check behind the QR code. Shows only first name + initial, course and date.</summary>
    [HttpGet("verify/{code}")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Verify(string code, CancellationToken cancellationToken)
    {
        var result = await certificates.VerifyAsync(code, cancellationToken);
        if (result is null) return NotFound(new { detail = "No certificate matches this code." });
        return Ok(new { result.Valid, result.Revoked, result.Code, result.StudentName, result.CourseName, result.IssuedAtUtc, academyName = await AcademyNameAsync(cancellationToken) });
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<IActionResult> PdfAsync(CertificateDto certificate, CancellationToken cancellationToken)
    {
        var academy = await AcademyNameAsync(cancellationToken);
        var pdf = CertificatePdfGenerator.Generate(new CertificatePdfData(certificate, academy, await VerifyUrlAsync(certificate.Code, cancellationToken)));
        var safeName = string.Concat($"{certificate.StudentName}-{certificate.CourseName}".Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
        return File(pdf, "application/pdf", $"certificate-{safeName}.pdf");
    }

    private async Task<string> AcademyNameAsync(CancellationToken cancellationToken)
    {
        var all = await settings.GetAllAsync(cancellationToken);
        return all.FirstOrDefault(x => x.Key == "brand.name")?.Value is { Length: > 0 } name ? name.Trim()
            : all.FirstOrDefault(x => x.Key == "org.name")?.Value is { Length: > 0 } org ? org.Trim()
            : TenantExecution.Current?.Code ?? "Meet to Manage";
    }

    /// <summary>The academy's portal address plus /t/&lt;academy&gt;/verify/&lt;code&gt;, so the link works from anywhere.</summary>
    private async Task<string> VerifyUrlAsync(string code, CancellationToken cancellationToken)
    {
        var configured = (await settings.GetAllAsync(cancellationToken)).FirstOrDefault(x => x.Key == "app.webBaseUrl")?.Value;
        var web = (string.IsNullOrWhiteSpace(configured) ? configuration["Frontend:BaseUrl"] : configured)?.TrimEnd('/')
            ?? $"{Request.Scheme}://{Request.Host}";
        var tenant = TenantExecution.Current?.Code is { Length: > 0 } t ? $"/t/{t}" : string.Empty;
        return $"{web}{tenant}/verify/{code}";
    }
}
