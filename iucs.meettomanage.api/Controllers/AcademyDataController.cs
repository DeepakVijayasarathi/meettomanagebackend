using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using iucs.meettomanage.api.Saas;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Controllers;

public sealed class CloseAcademyRequest
{
    /// <summary>The academy's web address (tenant code), typed by the admin to confirm.</summary>
    [Required, MaxLength(50)] public string ConfirmCode { get; set; } = "";
    [MaxLength(1000)] public string? Reason { get; set; }
}

/// <summary>
/// An academy's own data rights: export everything it holds as CSV files, and close the academy.
/// Admin-only and available on every plan (no module permission), as data portability and deletion
/// requests are not a paid feature.
/// </summary>
[ApiController, Route("api/academy-data"), Authorize(Roles = nameof(UserRole.Admin))]
public sealed class AcademyDataController(MeetToManageDbContext db, ILogger<AcademyDataController> logger) : ControllerBase
{
    /// <summary>A ZIP of CSV files: people, courses, batches, enrolments, sessions, attendance and invoices.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(x => x.Role).ThenBy(x => x.FirstName)
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.Email, x.Phone, Role = x.Role.ToString(), Status = x.Status.ToString(), x.CreatedAtUtc, x.LastLoginAtUtc })
            .ToListAsync(ct);
        var parents = await db.ParentProfiles.AsNoTracking()
            .Select(x => new { ParentId = x.Id, x.UserId, x.User.FirstName, x.User.LastName, x.User.Email, x.User.Phone, x.Address, x.TermsAcceptedAtUtc })
            .ToListAsync(ct);
        var students = await db.Children.AsNoTracking().OrderBy(x => x.FirstName)
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.DateOfBirth, Gender = x.Gender.ToString(), x.AcademicLevel, x.IsActive, x.ParentProfileId, ParentEmail = x.ParentProfile.User.Email, x.CreatedAtUtc })
            .ToListAsync(ct);
        var courses = await db.Courses.AsNoTracking()
            .Select(x => new { x.Id, x.Name, Category = x.CourseCategory.Name, Department = x.Department.Name, Type = x.Type.ToString(), x.DurationMinutes, x.Price, x.TotalSessions, x.IsActive })
            .ToListAsync(ct);
        var batches = await db.Batches.AsNoTracking()
            .Select(x => new { x.Id, x.Name, Course = x.Course.Name, TeacherEmail = x.TeacherProfile.User.Email, x.Capacity, Status = x.Status.ToString(), x.StartDate, x.EndDate })
            .ToListAsync(ct);
        var enrolments = await db.BatchEnrollments.AsNoTracking()
            .Select(x => new { x.BatchId, Batch = x.Batch.Name, x.ChildId, Student = x.Child.FirstName + " " + x.Child.LastName, Status = x.Status.ToString(), x.CreatedAtUtc })
            .ToListAsync(ct);
        var sessions = await db.ClassSessions.AsNoTracking().OrderBy(x => x.ScheduledStartAtUtc)
            .Select(x => new { x.Id, x.BatchId, Batch = x.Batch != null ? x.Batch.Name : null, TeacherEmail = x.TeacherProfile.User.Email, Type = x.Type.ToString(), Status = x.Status.ToString(), x.ScheduledStartAtUtc, x.ScheduledEndAtUtc, x.ActualStartAtUtc, x.ActualEndAtUtc, x.CancellationReason })
            .ToListAsync(ct);
        var attendance = await db.SessionAttendances.AsNoTracking()
            .Select(x => new { x.ClassSessionId, Participant = x.ParticipantType.ToString(), x.ChildId, x.TeacherProfileId, Status = x.Status.ToString(), x.JoinedAtUtc, x.LeftAtUtc })
            .ToListAsync(ct);
        var invoices = await db.Invoices.AsNoTracking().OrderBy(x => x.IssuedAtUtc)
            .Select(x => new { x.InvoiceNumber, ParentEmail = x.ParentProfile.User.Email, x.ChildId, Course = x.Course != null ? x.Course.Name : null, x.Amount, x.AmountPaid, x.Currency, Status = x.Status.ToString(), x.DueDate, x.IssuedAtUtc, x.PaidAtUtc })
            .ToListAsync(ct);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddCsv(zip, "users.csv", users);
            AddCsv(zip, "parents.csv", parents);
            AddCsv(zip, "students.csv", students);
            AddCsv(zip, "courses.csv", courses);
            AddCsv(zip, "batches.csv", batches);
            AddCsv(zip, "batch-enrolments.csv", enrolments);
            AddCsv(zip, "sessions.csv", sessions);
            AddCsv(zip, "attendance.csv", attendance);
            AddCsv(zip, "invoices.csv", invoices);
            var readme = zip.CreateEntry("README.txt");
            await using var writer = new StreamWriter(readme.Open(), new UTF8Encoding(false));
            await writer.WriteAsync($"Data export created {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.\r\nTimes are UTC. PINs and other sign-in secrets are never exported.\r\n");
        }
        var code = HttpContext.RequestServices.GetService<TenantContext>()?.TenantCode ?? "academy";
        logger.LogInformation("Academy data export downloaded for {TenantCode}.", code);
        return File(buffer.ToArray(), "application/zip", $"{code}-data-{DateTime.UtcNow:yyyyMMdd}.zip");
    }

    /// <summary>
    /// Closes the academy at once: the subscription ends, unpaid platform invoices are voided and the workspace is
    /// suspended, and the platform operator is told. Nothing is deleted here; the operator removes the data later.
    /// </summary>
    [HttpPost("close")]
    public async Task<IActionResult> Close(CloseAcademyRequest request, CancellationToken ct)
    {
        var services = HttpContext.RequestServices;
        var tenant = services.GetService<TenantContext>();
        if (tenant?.TenantId is not { } tenantId || services.GetService<SaasSubscriptionService>() is not { } subscriptions)
            throw new DomainValidationException("This installation has no academies to close.");
        if (!string.Equals(request.ConfirmCode.Trim(), tenant.TenantCode, StringComparison.OrdinalIgnoreCase))
            throw new DomainValidationException("Type your academy's web address exactly to confirm.");

        var adminEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email") ?? "the academy admin";
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "no reason given" : request.Reason.Trim();
        var master = services.GetRequiredService<MasterDbContext>();
        var sub = await master.TenantSubscriptions.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (sub is not null)
        {
            master.TenantSubscriptionEvents.Add(new TenantSubscriptionEvent
            {
                TenantId = tenantId, SubscriptionId = sub.Id, EventType = "closure_requested", Actor = adminEmail,
                Detail = Truncate($"Academy closed by {adminEmail}. Reason: {reason}. Data kept until the operator deletes it.", 1000),
            });
            await master.SaveChangesAsync(ct);
            if (sub.Status is not (SaasSubscriptionStatus.Cancelled or SaasSubscriptionStatus.Expired))
                await subscriptions.CancelAsync(tenantId, immediate: true, actor: adminEmail, ct);
        }
        else
        {
            // An academy from before billing has no subscription to cancel: suspend the workspace directly, or
            // "closing" would sign the admin out while everyone else kept working.
            var record = await master.Tenants.SingleAsync(x => x.Id == tenantId, ct);
            if (record.Status == domain.Entities.Tenancy.TenantStatus.Active) record.Status = domain.Entities.Tenancy.TenantStatus.Suspended;
            await master.SaveChangesAsync(ct);
            logger.LogWarning("Academy {TenantCode} (no subscription) was closed by {Admin}. Reason: {Reason}", tenant.TenantCode, adminEmail, reason);
        }

        await TellOperatorAsync(services, tenant.TenantCode ?? tenantId.ToString(), adminEmail, reason, ct);
        return Ok(new { closed = true });
    }

    private async Task TellOperatorAsync(IServiceProvider services, string code, string adminEmail, string reason, CancellationToken ct)
    {
        try
        {
            var to = services.GetService<IOptions<SaasOptions>>()?.Value.SupportEmail is { Length: > 0 } support
                ? support : services.GetRequiredService<IConfiguration>()["Marketing:LeadAlertEmail"];
            if (string.IsNullOrWhiteSpace(to) || services.GetService<PlatformEmailSender>() is not { } email) return;
            await email.SendAsync(to, $"Academy closed: {code}",
                $"<p>The academy <b>{System.Net.WebUtility.HtmlEncode(code)}</b> was closed by {System.Net.WebUtility.HtmlEncode(adminEmail)}.</p>" +
                $"<p>Reason: {System.Net.WebUtility.HtmlEncode(reason)}</p><p>The workspace is suspended and its data is kept. Delete it from the platform console once any retention period has passed.</p>", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not e-mail the operator about the closure of {TenantCode}.", code);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    // ---------------------------------------------------------------- CSV

    private static void AddCsv<T>(ZipArchive zip, string name, IReadOnlyList<T> rows)
    {
        var props = typeof(T).GetProperties();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", props.Select(p => Cell(p.Name))));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", props.Select(p => Cell(Format(p.GetValue(row))))));
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        // BOM so Excel opens UTF-8 (Indian and Arabic names) correctly.
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        stream.Write(bytes);
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "yes" : "no",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>RFC 4180 quoting, plus a leading apostrophe on formula-like text so a spreadsheet never executes it.</summary>
    public static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
