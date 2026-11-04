using iucs.meettomanage.api.Controllers;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class MitoUsageTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly MitoUsageService _usage;

    public MitoUsageTests() => _usage = new MitoUsageService(_db.UnitOfWork);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task DailyLimit_StopsAStudentOnceUsedUp_AndUnlimitedNeverStops()
    {
        var (_, _, asha) = await SeedAsync("Asha");
        Assert.Equal(2, await _usage.RecordAsync(asha.Id, "doubt", 3));
        Assert.Equal(1, await _usage.RecordAsync(asha.Id, "plan", 3));
        Assert.Equal(0, await _usage.RecordAsync(asha.Id, "quiz", 3));
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => _usage.RecordAsync(asha.Id, "doubt", 3));
        Assert.Contains("3", error.Message);
        Assert.Null(await _usage.RecordAsync(asha.Id, "general", 0));
    }

    [Fact]
    public void DailyLimitSetting_FallsBackToTheDefaultWhenUnsetOrInvalid()
    {
        Assert.Equal(MitoUsageService.DefaultDailyLimit, MitoController.DailyLimit(null));
        Assert.Equal(MitoUsageService.DefaultDailyLimit, MitoController.DailyLimit("lots"));
        Assert.Equal(MitoUsageService.DefaultDailyLimit, MitoController.DailyLimit("-5"));
        Assert.Equal(0, MitoController.DailyLimit("0"));
        Assert.Equal(15, MitoController.DailyLimit("15"));
    }

    [Fact]
    public async Task Insights_CountRequestsByModeAndStudent_ScopedForTeachers()
    {
        var (teacher, batch, asha) = await SeedAsync("Asha");
        var (_, _, ravi) = await SeedAsync("Ravi");
        await _usage.RecordAsync(asha.Id, "doubt", 0);
        await _usage.RecordAsync(asha.Id, "doubt", 0);
        await _usage.RecordAsync(asha.Id, "quiz", 0);
        await _usage.RecordAsync(ravi.Id, "general", 2);
        await _usage.RecordAsync(ravi.Id, "general", 2);

        var all = await _usage.GetInsightsAsync(30, 2, null);
        Assert.Equal(5, all.TotalRequests);
        Assert.Equal(2, all.ActiveStudents);
        Assert.Equal(2, all.StudentsAtLimitToday); // Ravi used 2 of 2; Asha 3 (recorded while unlimited)
        Assert.Equal(2, all.ByMode["doubt"]);
        Assert.Equal(1, all.ByMode["quiz"]);
        Assert.Equal(2, all.ByMode["general"]);
        Assert.Equal(30, all.ByDay.Count);
        Assert.Equal(5, all.ByDay[^1].Requests);
        Assert.Equal("Asha Kid", all.TopStudents[0].Name);

        // Asha's teacher sees only Asha.
        var mine = await _usage.GetInsightsAsync(30, 2, teacher.Id);
        Assert.Equal(3, mine.TotalRequests);
        Assert.Single(mine.TopStudents);
    }

    [Fact]
    public async Task FlaggedAnswers_ReachTheStudentsTeacher_WhoCanResolveThem()
    {
        var (teacher, _, asha) = await SeedAsync("Asha");
        var (otherTeacher, _, ravi) = await SeedAsync("Ravi");
        await _usage.FlagAsync(new FlagMitoAnswerRequest { ChildId = asha.Id, Mode = "doubt", Question = "What is 7 x 8?", Answer = "54", Reason = "Wrong maths" });
        await _usage.FlagAsync(new FlagMitoAnswerRequest { ChildId = ravi.Id, Question = "Hi", Answer = "Hello" });

        var forTeacher = Assert.Single(await _usage.ListFlagsAsync(true, teacher.Id));
        Assert.Equal("54", forTeacher.Answer);
        Assert.Equal(2, (await _usage.ListFlagsAsync(true, null)).Count);
        await Assert.ThrowsAsync<NotFoundException>(() => _usage.ResolveFlagAsync(forTeacher.Id, otherTeacher.Id, otherTeacher.Id, "x"));

        var resolved = await _usage.ResolveFlagAsync(forTeacher.Id, teacher.Id, teacher.Id, "7 x 8 = 56. Mito was wrong.");
        Assert.NotNull(resolved.ResolvedAtUtc);
        Assert.Empty(await _usage.ListFlagsAsync(true, teacher.Id));
        Assert.Single(await _usage.ListFlagsAsync(false, teacher.Id));
        Assert.Equal(1, (await _usage.GetInsightsAsync(30, 0, null)).OpenFlags);
    }

    private async Task<(User Teacher, Batch Batch, Child Child)> SeedAsync(string childName)
    {
        var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
        var teacher = new TeacherProfile { UserId = teacherUser.Id };
        var category = new CourseCategory { Name = $"Cat-{Guid.NewGuid():N}", DepartmentId = WellKnownDepartments.Phonics };
        var course = new Course { CourseCategory = category, Name = "Maths", Type = CourseType.Group, DurationMinutes = 45, Price = 100, TotalSessions = 10, DepartmentId = WellKnownDepartments.Phonics };
        var batch = new Batch { Course = course, TeacherProfile = teacher, Name = $"Batch {childName}", Capacity = 5 };
        var parent = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
        var profile = new ParentProfile { UserId = parent.Id };
        var child = new Child { ParentProfile = profile, FirstName = childName, LastName = "Kid" };
        _db.Context.AddRange(teacher, category, course, batch, profile, child);
        await _db.Context.SaveChangesAsync();
        _db.Context.Add(new BatchEnrollment { BatchId = batch.Id, ChildId = child.Id, Status = EnrollmentStatus.Active });
        await _db.Context.SaveChangesAsync();
        return (teacherUser, batch, child);
    }
}
