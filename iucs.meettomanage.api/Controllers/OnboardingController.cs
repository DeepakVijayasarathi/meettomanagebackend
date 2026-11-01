using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Controllers;

/// <summary>
/// Progress counts for the admin dashboard's "Get started" checklist. Plain counts of the academy's
/// own data, so it works on every plan and needs no module permission beyond being its Admin.
/// </summary>
[ApiController, Route("api/onboarding"), Authorize(Roles = nameof(UserRole.Admin))]
public sealed class OnboardingController(MeetToManageDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(new
    {
        courses = await db.Courses.CountAsync(ct),
        batches = await db.Batches.CountAsync(ct),
        teachers = await db.Users.CountAsync(x => x.Role == UserRole.Teacher && x.Status != UserStatus.Inactive, ct),
        parents = await db.Users.CountAsync(x => x.Role == UserRole.Parent && x.Status != UserStatus.Inactive, ct),
        students = await db.Children.CountAsync(ct),
        sessionsScheduled = await db.ClassSessions.CountAsync(ct),
        sessionsCompleted = await db.ClassSessions.CountAsync(x => x.Status == SessionStatus.Completed, ct),
    });
}
