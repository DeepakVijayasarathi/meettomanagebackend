using System.Security.Claims;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Mito;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Quizzes;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;
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
    private readonly IUnitOfWork _unitOfWork;

    public MitoController(IParentPortalService portal, IMitoAssistant mito, IUnitOfWork unitOfWork)
    {
        _portal = portal;
        _mito = mito;
        _unitOfWork = unitOfWork;
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

    /// <summary>Returns only teacher/admin-authored questions from this student's enrolled courses. Correct answers stay server-side.</summary>
    [HttpPost("quiz")]
    [EnableRateLimiting("mito-chat")]
    public async Task<ActionResult<IReadOnlyList<MitoQuizQuestionDto>>> Quiz(MitoQuizRequest request, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await RequireChildAsync(userId, request.ChildId, cancellationToken);
        var scopes = await _unitOfWork.Repository<BatchEnrollment>().Query()
            .Where(x => x.ChildId == request.ChildId && x.Status == EnrollmentStatus.Active)
            .Select(x => new { x.Batch.CourseId, x.Batch.Course.DepartmentId }).ToListAsync(cancellationToken);
        var courseIds = scopes.Select(x => x.CourseId).Distinct().ToList();
        var departmentIds = scopes.Select(x => x.DepartmentId).Distinct().ToList();
        var bank = await _unitOfWork.Repository<QuizQuestion>().Query()
            .Include(x => x.Course).Include(x => x.Options)
            .Where(x => (x.CourseId != null && courseIds.Contains(x.CourseId.Value))
                || (x.CourseId == null && departmentIds.Contains(x.DepartmentId)))
            .ToListAsync(cancellationToken);
        var selected = bank.OrderBy(_ => Random.Shared.Next()).Take(request.Count).Select(x => new MitoQuizQuestionDto
        {
            Id = x.Id, Prompt = x.Prompt, CourseName = x.Course?.Name,
            Options = x.Options.OrderBy(o => o.DisplayOrder).Select(o => new MitoQuizOptionDto { Id = o.Id, Text = o.Text }).ToList(),
        }).ToList();
        return Ok(selected);
    }

    /// <summary>Grades one approved-bank answer on the server, revealing the answer only after submission.</summary>
    [HttpPost("quiz/answer")]
    [EnableRateLimiting("mito-chat")]
    public async Task<ActionResult<MitoQuizAnswerResponse>> AnswerQuiz(MitoQuizAnswerRequest request, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await RequireChildAsync(userId, request.ChildId, cancellationToken);
        var scopes = await _unitOfWork.Repository<BatchEnrollment>().Query()
            .Where(x => x.ChildId == request.ChildId && x.Status == EnrollmentStatus.Active)
            .Select(x => new { x.Batch.CourseId, x.Batch.Course.DepartmentId }).ToListAsync(cancellationToken);
        var courseIds = scopes.Select(x => x.CourseId).Distinct().ToList();
        var departmentIds = scopes.Select(x => x.DepartmentId).Distinct().ToList();
        var question = await _unitOfWork.Repository<QuizQuestion>().Query().Include(x => x.Options)
            .FirstOrDefaultAsync(x => x.Id == request.QuestionId
                && ((x.CourseId != null && courseIds.Contains(x.CourseId.Value))
                    || (x.CourseId == null && departmentIds.Contains(x.DepartmentId))), cancellationToken)
            ?? throw new NotFoundException("Quiz question not found.");
        if (!question.Options.Any(x => x.Id == request.OptionId))
            throw new DomainValidationException("That answer option does not belong to this question.");
        var correct = question.Options.Single(x => x.IsCorrect);
        var isCorrect = correct.Id == request.OptionId;
        return Ok(new MitoQuizAnswerResponse
        {
            Correct = isCorrect, CorrectOptionId = correct.Id, CorrectAnswer = correct.Text,
            Feedback = isCorrect ? "Correct — well done!" : $"Not quite. The correct answer is {correct.Text}. Ask Mito to explain the concept if you want help.",
        });
    }

    private async Task RequireChildAsync(Guid userId, Guid childId, CancellationToken cancellationToken)
    {
        var dashboard = await _portal.GetDashboardAsync(userId, cancellationToken);
        if (dashboard.Children.All(x => x.ChildId != childId)) throw new NotFoundException("Student not found.");
    }
}
