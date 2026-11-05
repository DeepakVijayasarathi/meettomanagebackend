using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class WaitlistTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakePushSender _push = new();
    private readonly WaitlistService _waitlist;

    public WaitlistTests()
    {
        var audit = new AuditLogService(_db.UnitOfWork, _db.CurrentUser);
        var templates = new EmailTemplateService(_db.UnitOfWork, audit, new MemoryCache(new MemoryCacheOptions()));
        var notifications = new NotificationService(_db.UnitOfWork, new FakeEmailSender(), templates, _push, NullLogger<NotificationService>.Instance);
        var batches = new BatchService(_db.UnitOfWork, audit, notifications);
        _waitlist = new WaitlistService(_db.UnitOfWork, batches, notifications, NullLogger<WaitlistService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task FullBatch_QueuesInOrder_AndOffersSeatsAsTheyOpen()
    {
        var admin = await _db.SeedUserAsync($"a-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);
        var batch = await SeedBatchAsync(capacity: 1);
        var (_, enrolled) = await SeedChildAsync("Asha");
        await EnrolAsync(batch, enrolled);
        var (firstParent, first) = await SeedChildAsync("Ravi");
        var (_, second) = await SeedChildAsync("Meera");

        await _waitlist.AddAsync(new AddToWaitlistRequest { BatchId = batch.Id, ChildId = first.Id, Note = "Prefers weekends" });
        await _waitlist.AddAsync(new AddToWaitlistRequest { BatchId = batch.Id, ChildId = second.Id });
        await Assert.ThrowsAsync<ConflictException>(() => _waitlist.AddAsync(new AddToWaitlistRequest { BatchId = batch.Id, ChildId = first.Id }));
        await Assert.ThrowsAsync<ConflictException>(() => _waitlist.AddAsync(new AddToWaitlistRequest { BatchId = batch.Id, ChildId = enrolled.Id }));

        var list = await _waitlist.ListAsync(batch.Id);
        Assert.Equal(new[] { "Ravi Kid", "Meera Kid" }, list.Select(x => x.ChildName));
        Assert.Equal(new[] { 1, 2 }, list.Select(x => x.Position));
        Assert.All(list, x => Assert.Equal(0, x.SeatsFree));

        // Full: nothing to offer.
        Assert.Equal(0, await _waitlist.OfferOpenSeatsAsync());

        // A seat opens: only the first in line is offered, once.
        var enrolment = await _db.Context.BatchEnrollments.SingleAsync(e => e.ChildId == enrolled.Id);
        enrolment.Status = EnrollmentStatus.Withdrawn;
        await _db.Context.SaveChangesAsync();
        Assert.Equal(1, await _waitlist.OfferOpenSeatsAsync());
        Assert.Equal(0, await _waitlist.OfferOpenSeatsAsync());
        list = await _waitlist.ListAsync(batch.Id);
        Assert.Equal(WaitlistStatus.Offered, list[0].Status);
        Assert.Equal(WaitlistStatus.Waiting, list[1].Status);
        Assert.Contains(_push.Sent, p => p.UserId == firstParent.Id && p.Title.StartsWith("A seat opened"));
        Assert.Contains(_push.Sent, p => p.UserId == admin.Id && p.Title.StartsWith("Waitlist: seat free"));

        // Enrolling uses the normal capacity rules and closes the entry.
        await _waitlist.EnrollAsync(list[0].Id);
        Assert.True(await _db.Context.BatchEnrollments.AnyAsync(e => e.ChildId == first.Id && e.BatchId == batch.Id && e.Status == EnrollmentStatus.Active));
        Assert.Single(await _waitlist.ListAsync(batch.Id));
        await Assert.ThrowsAsync<DomainValidationException>(() => _waitlist.EnrollAsync(list[1].Id)); // batch full again
    }

    [Fact]
    public async Task RemovedStudents_CanRejoinAtTheBackOfTheQueue()
    {
        var batch = await SeedBatchAsync(capacity: 1);
        var (_, a) = await SeedChildAsync("Asha");
        var (_, b) = await SeedChildAsync("Ravi");
        var entryA = await _waitlist.AddAsync(new AddToWaitlistRequest { BatchId = batch.Id, ChildId = a.Id });
        await _waitlist.AddAsync(new AddToWaitlistRequest { BatchId = batch.Id, ChildId = b.Id });

        await _waitlist.RemoveAsync(entryA.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => _waitlist.RemoveAsync(entryA.Id));
        await _waitlist.AddAsync(new AddToWaitlistRequest { BatchId = batch.Id, ChildId = a.Id });

        Assert.Equal(new[] { "Ravi Kid", "Asha Kid" }, (await _waitlist.ListAsync(batch.Id)).Select(x => x.ChildName));
    }

    private async Task<Batch> SeedBatchAsync(int capacity)
    {
        var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
        var teacher = new TeacherProfile { UserId = teacherUser.Id };
        var category = new CourseCategory { Name = $"Cat-{Guid.NewGuid():N}", DepartmentId = WellKnownDepartments.Phonics };
        var course = new Course { CourseCategory = category, Name = "Phonics", Type = CourseType.Group, DurationMinutes = 45, Price = 100, TotalSessions = 10, DepartmentId = WellKnownDepartments.Phonics };
        var batch = new Batch { Course = course, TeacherProfile = teacher, Name = "Phonics A", Capacity = capacity };
        _db.Context.AddRange(teacher, category, course, batch);
        await _db.Context.SaveChangesAsync();
        return batch;
    }

    private async Task<(User Parent, Child Child)> SeedChildAsync(string first)
    {
        var parent = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
        var profile = new ParentProfile { UserId = parent.Id };
        var child = new Child { ParentProfile = profile, FirstName = first, LastName = "Kid" };
        _db.Context.AddRange(profile, child);
        await _db.Context.SaveChangesAsync();
        return (parent, child);
    }

    private async Task EnrolAsync(Batch batch, Child child)
    {
        _db.Context.Add(new BatchEnrollment { BatchId = batch.Id, ChildId = child.Id, Status = EnrollmentStatus.Active });
        await _db.Context.SaveChangesAsync();
    }
}
