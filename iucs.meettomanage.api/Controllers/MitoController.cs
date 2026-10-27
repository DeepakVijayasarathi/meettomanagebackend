using System.Security.Claims;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Mito;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace iucs.meettomanage.api.Controllers;

[ApiController]
[Route("api/mito")]
[Authorize(Roles = nameof(UserRole.Parent))]
public class MitoController : ControllerBase
{
    private readonly IParentPortalService _portal;
    private readonly IMitoAssistant _mito;

    public MitoController(IParentPortalService portal, IMitoAssistant mito)
    {
        _portal = portal;
        _mito = mito;
    }

    [HttpPost("chat")]
    [EnableRateLimiting("mito-chat")]
    public async Task<ActionResult<MitoChatResponse>> Chat(MitoChatRequest request, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var dashboard = await _portal.GetDashboardAsync(userId, cancellationToken);
        var child = dashboard.Children.SingleOrDefault(x => x.ChildId == request.ChildId)
            ?? throw new NotFoundException("Student not found.");
        var now = DateTime.UtcNow;
        var sessions = await _portal.GetScheduleAsync(userId, now.AddDays(-30), now.AddDays(30), cancellationToken);
        var relevant = sessions.Where(s => s.ChildIds?.Contains(request.ChildId) == true).ToList();
        var upcoming = relevant.Where(s => s.ScheduledEndAtUtc >= now)
            .OrderBy(s => s.ScheduledStartAtUtc).Take(8)
            .Select(s => $"{s.BatchName ?? "Class"} with {s.TeacherName} at {s.ScheduledStartAtUtc:u}").ToList();
        var recent = relevant.Where(s => s.Status == SessionStatus.Completed && !string.IsNullOrWhiteSpace(s.Summary))
            .OrderByDescending(s => s.ScheduledStartAtUtc).Take(5)
            .Select(s => $"{s.BatchName ?? "Class"}: {s.Summary}").ToList();
        var context = new MitoStudentContext(child.Name, child.AcademicLevel, child.ClassesCompleted,
            child.ClassesRemaining, child.AttendancePercent, upcoming, recent);
        return Ok(await _mito.ReplyAsync(context, request.Messages, request.Mode, cancellationToken));
    }

    [HttpGet("today")]
    public async Task<ActionResult<MitoTodayResponse>> Today([FromQuery] Guid childId, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var dashboard = await _portal.GetDashboardAsync(userId, cancellationToken);
        var child = dashboard.Children.SingleOrDefault(x => x.ChildId == childId)
            ?? throw new NotFoundException("Student not found.");
        var now = DateTime.UtcNow;
        var end = now.Date.AddDays(1);
        var sessions = await _portal.GetScheduleAsync(userId, now.Date, end, cancellationToken);
        return Ok(new MitoTodayResponse
        {
            StudentName = child.Name,
            GeneratedAtUtc = now,
            Items = sessions.Where(x => x.ChildIds?.Contains(childId) == true)
                .OrderBy(x => x.ScheduledStartAtUtc)
                .Select(x => new MitoTodayItem
                {
                    SessionId = x.Id, Title = x.BatchName ?? "Class", TeacherName = x.TeacherName,
                    StartsAtUtc = x.ScheduledStartAtUtc, EndsAtUtc = x.ScheduledEndAtUtc, Status = x.Status.ToString(),
                }).ToList(),
        });
    }

    [HttpGet("progress")]
    public async Task<ActionResult<MitoProgressResponse>> Progress([FromQuery] Guid childId, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var dashboard = await _portal.GetDashboardAsync(userId, cancellationToken);
        var child = dashboard.Children.SingleOrDefault(x => x.ChildId == childId)
            ?? throw new NotFoundException("Student not found.");
        var attendance = child.AttendancePercent is null ? "Attendance will appear after classes are recorded."
            : $"Attendance is {child.AttendancePercent:0.#}%.";
        return Ok(new MitoProgressResponse
        {
            StudentName = child.Name, AcademicLevel = child.AcademicLevel,
            ClassesCompleted = child.ClassesCompleted, ClassesRemaining = child.ClassesRemaining,
            AttendancePercent = child.AttendancePercent,
            Summary = $"{child.ClassesCompleted} classes completed and {child.ClassesRemaining} upcoming. {attendance}",
        });
    }
}
