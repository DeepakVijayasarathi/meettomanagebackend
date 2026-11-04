using System.Text;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Learning;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests
{
    public class HomeworkTests : IDisposable
    {
        private readonly TestDatabase _db = new();
        private readonly FakePushSender _push = new();
        private readonly MemoryFileStorage _storage = new();
        private readonly AssignmentService _service;
        private readonly NotificationService _notifications;

        public HomeworkTests()
        {
            var audit = new AuditLogService(_db.UnitOfWork, _db.CurrentUser);
            var templates = new EmailTemplateService(_db.UnitOfWork, audit, new MemoryCache(new MemoryCacheOptions()));
            _notifications = new NotificationService(_db.UnitOfWork, new FakeEmailSender(), templates, _push, NullLogger<NotificationService>.Instance);
            _service = new AssignmentService(_db.UnitOfWork, _storage, _notifications, NullLogger<AssignmentService>.Instance);
        }

        public void Dispose() => _db.Dispose();

        [Fact]
        public async Task Homework_FlowsFromTeacherToFamilyAndBack()
        {
            var (teacher, batch) = await SeedBatchAsync();
            var (parent, child) = await SeedEnrolledChildAsync(batch.Id, "Asha");
            var actor = new AssignmentActor(teacher.Id, IsStaff: false);

            var created = await _service.CreateAsync(actor, new SaveAssignmentRequest
            {
                BatchId = batch.Id, Kind = AssignmentKind.Homework, Title = "Phonics sheet 3",
                DueAtUtc = DateTime.UtcNow.AddDays(2), MaxMarks = 10, Topic = "Digraphs",
            }, File("sheet.pdf"));

            Assert.Equal(1, created.StudentCount);
            Assert.Equal("sheet.pdf", created.AttachmentFileName);
            Assert.Contains(_push.Sent, p => p.UserId == parent.Id && p.Title.StartsWith("New homework"));

            // The family sees it, with a link target on the notification.
            var mine = Assert.Single(await _service.ListForParentAsync(parent.Id, null));
            Assert.True(mine.CanSubmit);
            Assert.Equal(AssignmentSubmissionStatus.Pending, mine.Status);
            var feed = await _notifications.GetFeedForUserAsync(parent.Id);
            Assert.Contains(feed.Items, n => n.TargetType == NotificationTargets.Homework && n.TargetId == created.Id);

            await Assert.ThrowsAsync<DomainValidationException>(() =>
                _service.SubmitAsync(parent.Id, mine.SubmissionId, new SubmitAssignmentRequest(), null));
            var handedIn = await _service.SubmitAsync(parent.Id, mine.SubmissionId, new SubmitAssignmentRequest { AnswerText = "Done" }, File("work.jpg"));
            Assert.Equal(AssignmentSubmissionStatus.Submitted, handedIn.Status);
            Assert.Equal("work.jpg", handedIn.SubmissionFileName);

            var detail = await _service.GetAsync(actor, created.Id);
            var row = Assert.Single(detail.Submissions);
            Assert.Equal("Asha Kid", row.ChildName);
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                _service.GradeAsync(actor, row.Id, new GradeSubmissionRequest { Marks = 11 }));
            await _service.GradeAsync(actor, row.Id, new GradeSubmissionRequest { Marks = 8, Feedback = "Nice" });

            var graded = Assert.Single(await _service.ListForParentAsync(parent.Id, null));
            Assert.Equal(8, graded.Marks);
            Assert.Equal("Nice", graded.Feedback);
            Assert.False(graded.CanSubmit);
            Assert.Contains(_push.Sent, p => p.UserId == parent.Id && p.Title.StartsWith("Homework checked"));
        }

        [Fact]
        public async Task TestMarks_StayHiddenUntilReleased()
        {
            var (teacher, batch) = await SeedBatchAsync();
            var (parentA, _) = await SeedEnrolledChildAsync(batch.Id, "Asha");
            var (parentB, _) = await SeedEnrolledChildAsync(batch.Id, "Ravi");
            var actor = new AssignmentActor(teacher.Id, IsStaff: false);

            await Assert.ThrowsAsync<DomainValidationException>(() => _service.CreateAsync(actor,
                new SaveAssignmentRequest { BatchId = batch.Id, Kind = AssignmentKind.Test, Title = "Unit test" }, null));
            var test = await _service.CreateAsync(actor,
                new SaveAssignmentRequest { BatchId = batch.Id, Kind = AssignmentKind.Test, Title = "Unit test", MaxMarks = 20 }, null);

            var rows = (await _service.GetAsync(actor, test.Id)).Submissions;
            await _service.GradeAsync(actor, rows[0].Id, new GradeSubmissionRequest { Marks = 18 });
            await Assert.ThrowsAsync<DomainValidationException>(() => _service.ReleaseResultsAsync(actor, test.Id));

            // Graded but unreleased: the family still sees no marks.
            var early = await _service.ListForParentAsync(parentA.Id, null);
            Assert.All(early, x => Assert.Null(x.Marks));
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                _service.SubmitAsync(parentA.Id, early[0].SubmissionId, new SubmitAssignmentRequest { AnswerText = "x" }, null));

            await _service.GradeAsync(actor, rows[1].Id, new GradeSubmissionRequest { Marks = 12 });
            await _service.ReleaseResultsAsync(actor, test.Id);

            var a = Assert.Single(await _service.ListForParentAsync(parentA.Id, null));
            var b = Assert.Single(await _service.ListForParentAsync(parentB.Id, null));
            Assert.True(a.ResultsReleased);
            Assert.Equal(15, a.ClassAverage);
            Assert.Equal(new decimal[] { 12, 18 }, new[] { a.Marks!.Value, b.Marks!.Value }.OrderBy(x => x));
            Assert.Equal(2, _push.Sent.Count(p => p.Title.StartsWith("Test results")));
        }

        [Fact]
        public async Task Access_IsLimitedToOwnBatchesAndOwnChildren()
        {
            var (teacher, batch) = await SeedBatchAsync();
            var (otherTeacher, _) = await SeedBatchAsync();
            var (parent, _) = await SeedEnrolledChildAsync(batch.Id, "Asha");
            var outsider = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            _db.Context.ParentProfiles.Add(new ParentProfile { UserId = outsider.Id });
            await _db.Context.SaveChangesAsync();

            var request = new SaveAssignmentRequest { BatchId = batch.Id, Kind = AssignmentKind.Homework, Title = "Read" };
            await Assert.ThrowsAsync<ForbiddenException>(() => _service.CreateAsync(new AssignmentActor(otherTeacher.Id, false), request, null));
            var created = await _service.CreateAsync(new AssignmentActor(teacher.Id, false), request, null);

            await Assert.ThrowsAsync<NotFoundException>(() => _service.GetAsync(new AssignmentActor(otherTeacher.Id, false), created.Id));
            Assert.Single(await _service.ListAsync(new AssignmentActor(Guid.NewGuid(), IsStaff: true), null));

            var submissionId = (await _service.ListForParentAsync(parent.Id, null)).Single().SubmissionId;
            Assert.Empty(await _service.ListForParentAsync(outsider.Id, null));
            await Assert.ThrowsAsync<NotFoundException>(() =>
                _service.SubmitAsync(outsider.Id, submissionId, new SubmitAssignmentRequest { AnswerText = "x" }, null));
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                _service.SubmitAsync(parent.Id, submissionId, new SubmitAssignmentRequest(), File("virus.exe")));
        }

        [Fact]
        public async Task OlderNotifications_GetALinkInferredFromTheirTemplate()
        {
            var parent = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            _db.Context.Notifications.Add(new domain.Entities.Communication.Notification
            {
                RecipientUserId = parent.Id, Type = NotificationType.General, Channel = NotificationChannel.Email,
                Body = "Invoice", TemplateKey = "invoice-issued", Status = NotificationStatus.Sent,
            });
            await _db.Context.SaveChangesAsync();

            var item = Assert.Single((await _notifications.GetFeedForUserAsync(parent.Id)).Items);
            Assert.Equal(NotificationTargets.Invoice, item.TargetType);
            Assert.Equal(NotificationTargets.Ticket, NotificationTargets.Infer(NotificationType.General, "support-ticket-updated"));
            Assert.Null(NotificationTargets.Infer(NotificationType.General, "pin-reset"));
        }

        private static UploadedFile File(string name)
        {
            var bytes = Encoding.UTF8.GetBytes("file");
            return new UploadedFile(new MemoryStream(bytes), name, bytes.Length);
        }

        private async Task<(User Teacher, Batch Batch)> SeedBatchAsync()
        {
            var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var teacher = new TeacherProfile { UserId = teacherUser.Id };
            var category = new CourseCategory { Name = $"Cat-{Guid.NewGuid():N}", DepartmentId = WellKnownDepartments.Phonics };
            var course = new Course
            {
                CourseCategory = category, Name = "Phonics", Type = CourseType.Group, DurationMinutes = 45,
                Price = 100, TotalSessions = 10, DepartmentId = WellKnownDepartments.Phonics,
            };
            var batch = new Batch { Course = course, TeacherProfile = teacher, Name = "Phonics A", Capacity = 5 };
            _db.Context.AddRange(teacher, category, course, batch);
            await _db.Context.SaveChangesAsync();
            return (teacherUser, batch);
        }

        private async Task<(User Parent, Child Child)> SeedEnrolledChildAsync(Guid batchId, string firstName)
        {
            var user = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var profile = new ParentProfile { UserId = user.Id };
            var child = new Child { ParentProfile = profile, FirstName = firstName, LastName = "Kid" };
            _db.Context.AddRange(profile, child);
            await _db.Context.SaveChangesAsync();
            _db.Context.Add(new BatchEnrollment { BatchId = batchId, ChildId = child.Id, Status = EnrollmentStatus.Active });
            await _db.Context.SaveChangesAsync();
            return (user, child);
        }

        private sealed class MemoryFileStorage : IFileStorage
        {
            private readonly Dictionary<string, byte[]> _files = new();

            public async Task<StoredFile> StoreAsync(Stream content, string originalFileName, CancellationToken cancellationToken = default)
            {
                using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, cancellationToken);
                var path = $"{Guid.NewGuid():N}{Path.GetExtension(originalFileName)}";
                _files[path] = buffer.ToArray();
                return new StoredFile { RelativePath = path, SizeBytes = buffer.Length };
            }

            public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
                Task.FromResult<Stream?>(_files.TryGetValue(relativePath, out var bytes) ? new MemoryStream(bytes) : null);
        }
    }
}
