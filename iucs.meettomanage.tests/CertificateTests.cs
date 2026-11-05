using iucs.meettomanage.api.Services;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class CertificateTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakePushSender _push = new();
    private readonly CertificateService _service;
    private readonly NotificationService _notifications;

    public CertificateTests()
    {
        var audit = new AuditLogService(_db.UnitOfWork, _db.CurrentUser);
        var templates = new EmailTemplateService(_db.UnitOfWork, audit, new MemoryCache(new MemoryCacheOptions()));
        _notifications = new NotificationService(_db.UnitOfWork, new FakeEmailSender(), templates, _push, NullLogger<NotificationService>.Instance);
        _service = new CertificateService(_db.UnitOfWork, _notifications, NullLogger<CertificateService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CompletedBatch_IssuesOneCertificatePerStudent_AndTellsTheFamily()
    {
        var batch = await SeedBatchAsync(completed: true);
        var (parent, asha) = await EnrolAsync(batch, "Asha", "Kumar");
        var (_, ravi) = await EnrolAsync(batch, "Ravi", "Shah", EnrollmentStatus.Withdrawn);
        var running = await SeedBatchAsync(completed: false);
        await EnrolAsync(running, "Meera", "Iyer");

        Assert.Equal(1, await _service.IssueForCompletedBatchesAsync(notify: true));
        Assert.Equal(0, await _service.IssueForCompletedBatchesAsync(notify: true)); // idempotent

        var mine = Assert.Single(await _service.ListForParentAsync(parent.Id));
        Assert.Equal("Asha Kumar", mine.StudentName);
        Assert.Equal("Phonics", mine.CourseName);
        Assert.Equal("Tara Teacher", mine.TeacherName);
        Assert.Equal(10, mine.Code.Length);
        Assert.Contains(_push.Sent, p => p.UserId == parent.Id && p.Title.StartsWith("Certificate ready"));
        var feed = await _notifications.GetFeedForUserAsync(parent.Id);
        Assert.Contains(feed.Items, n => n.TargetType == NotificationTargets.Certificate && n.TargetId == mine.Id);
        Assert.DoesNotContain(await _service.ListAsync(null), c => c.ChildId == ravi.Id);
    }

    [Fact]
    public async Task Verification_ShowsOnlyAPublicSafeName_AndRevocationIsVisible()
    {
        var batch = await SeedBatchAsync(completed: true);
        var (parent, _) = await EnrolAsync(batch, "Asha", "Kumar");
        await _service.IssueForCompletedBatchesAsync(notify: false);
        var certificate = (await _service.ListForParentAsync(parent.Id))[0];

        var check = await _service.VerifyAsync(certificate.Code.ToLowerInvariant().Insert(4, "-"));
        Assert.NotNull(check);
        Assert.True(check!.Valid);
        Assert.Equal("Asha K.", check.StudentName);
        Assert.Null(await _service.VerifyAsync("NOPE123456"));

        await _service.RevokeAsync(certificate.Id, "Issued by mistake");
        var revoked = await _service.VerifyAsync(certificate.Code);
        Assert.False(revoked!.Valid);
        Assert.True(revoked.Revoked);
        Assert.Empty(await _service.ListForParentAsync(parent.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetForParentAsync(parent.Id, certificate.Id));

        // Re-issuing restores the same code.
        var restored = await _service.IssueAsync(new IssueCertificateRequest { ChildId = certificate.ChildId, BatchId = batch.Id });
        Assert.Equal(certificate.Code, restored.Code);
        Assert.Null(restored.RevokedAtUtc);
        await Assert.ThrowsAsync<ConflictException>(() => _service.IssueAsync(new IssueCertificateRequest { ChildId = certificate.ChildId, BatchId = batch.Id }));
    }

    [Fact]
    public async Task ManualCertificates_NeedACourseName_AndParentsOnlySeeTheirOwn()
    {
        var batch = await SeedBatchAsync(completed: false);
        var (parent, child) = await EnrolAsync(batch, "Asha", "Kumar");
        var (otherParent, _) = await EnrolAsync(batch, "Ravi", "Shah");

        await Assert.ThrowsAsync<DomainValidationException>(() => _service.IssueAsync(new IssueCertificateRequest { ChildId = child.Id }));
        var manual = await _service.IssueAsync(new IssueCertificateRequest { ChildId = child.Id, CourseName = "Summer Reading Challenge" });
        Assert.Equal("Summer Reading Challenge", manual.CourseName);
        Assert.Equal(manual.Id, (await _service.GetForParentAsync(parent.Id, manual.Id)).Id);
        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetForParentAsync(otherParent.Id, manual.Id));
    }

    [Fact]
    public void Pdf_RendersWithAQrCode()
    {
        var pdf = CertificatePdfGenerator.Generate(new CertificatePdfData(
            new CertificateDto { Code = "ABCDEFGH23", StudentName = "Asha Kumar", CourseName = "Phonics", TeacherName = "Tara Teacher", IssuedAtUtc = DateTime.UtcNow },
            "Sunrise Academy", "https://app.example.com/t/sunrise/verify/ABCDEFGH23"));
        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(CertificatePdfGenerator.QrPng("x").Length > 100);
        Assert.Equal("Asha", CertificateService.PublicName("Asha"));
        Assert.Equal("Asha K.", CertificateService.PublicName("  Asha   kumar "));
    }

    private async Task<Batch> SeedBatchAsync(bool completed)
    {
        var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
        teacherUser.FirstName = "Tara";
        teacherUser.LastName = "Teacher";
        var teacher = new TeacherProfile { UserId = teacherUser.Id };
        var category = new CourseCategory { Name = $"Cat-{Guid.NewGuid():N}", DepartmentId = WellKnownDepartments.Phonics };
        var course = new Course { CourseCategory = category, Name = "Phonics", Type = CourseType.Group, DurationMinutes = 45, Price = 100, TotalSessions = 10, DepartmentId = WellKnownDepartments.Phonics };
        var batch = new Batch
        {
            Course = course, TeacherProfile = teacher, Name = $"Batch {Guid.NewGuid():N}"[..12], Capacity = 5,
            Status = completed ? BatchStatus.Dormant : BatchStatus.Active, CompletedAtUtc = completed ? DateTime.UtcNow.AddDays(-1) : null,
        };
        _db.Context.AddRange(teacher, category, course, batch);
        await _db.Context.SaveChangesAsync();
        return batch;
    }

    private async Task<(User Parent, Child Child)> EnrolAsync(Batch batch, string first, string last, EnrollmentStatus status = EnrollmentStatus.Active)
    {
        var parent = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
        var profile = new ParentProfile { UserId = parent.Id };
        var child = new Child { ParentProfile = profile, FirstName = first, LastName = last };
        _db.Context.AddRange(profile, child);
        await _db.Context.SaveChangesAsync();
        _db.Context.Add(new BatchEnrollment { BatchId = batch.Id, ChildId = child.Id, Status = status });
        await _db.Context.SaveChangesAsync();
        return (parent, child);
    }
}
