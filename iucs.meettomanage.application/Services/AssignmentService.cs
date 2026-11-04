using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Learning;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Learning;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace iucs.meettomanage.application.Services
{
    public interface IAssignmentService
    {
        Task<IReadOnlyList<AssignmentDto>> ListAsync(AssignmentActor actor, Guid? batchId, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<AssignableBatchDto>> ListBatchesAsync(AssignmentActor actor, CancellationToken cancellationToken = default);
        Task<AssignmentDetailDto> GetAsync(AssignmentActor actor, Guid id, CancellationToken cancellationToken = default);
        Task<AssignmentDetailDto> CreateAsync(AssignmentActor actor, SaveAssignmentRequest request, UploadedFile? file, CancellationToken cancellationToken = default);
        Task<AssignmentDetailDto> UpdateAsync(AssignmentActor actor, Guid id, SaveAssignmentRequest request, UploadedFile? file, CancellationToken cancellationToken = default);
        Task DeleteAsync(AssignmentActor actor, Guid id, CancellationToken cancellationToken = default);
        Task<AssignmentSubmissionDto> GradeAsync(AssignmentActor actor, Guid submissionId, GradeSubmissionRequest request, CancellationToken cancellationToken = default);
        Task<AssignmentDetailDto> ReleaseResultsAsync(AssignmentActor actor, Guid id, CancellationToken cancellationToken = default);
        Task<AttachmentDownload> GetAttachmentForStaffAsync(AssignmentActor actor, Guid id, CancellationToken cancellationToken = default);
        Task<AttachmentDownload> GetSubmissionFileForStaffAsync(AssignmentActor actor, Guid submissionId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<FamilyAssignmentDto>> ListForParentAsync(Guid parentUserId, Guid? childId, CancellationToken cancellationToken = default);
        Task<FamilyAssignmentDto> SubmitAsync(Guid parentUserId, Guid submissionId, SubmitAssignmentRequest request, UploadedFile? file, CancellationToken cancellationToken = default);
        Task<AttachmentDownload> GetAttachmentForParentAsync(Guid parentUserId, Guid submissionId, CancellationToken cancellationToken = default);
        Task<AttachmentDownload> GetSubmissionFileForParentAsync(Guid parentUserId, Guid submissionId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Homework and tests: teachers set work for a batch, families hand it in, teachers grade it,
    /// and test marks reach families only once the teacher releases results.
    /// </summary>
    public class AssignmentService : IAssignmentService
    {
        public const long MaxFileBytes = 15 * 1024 * 1024;

        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".heic", ".webp", ".doc", ".docx", ".txt",
        };

        private readonly IUnitOfWork _unitOfWork;
        private readonly IFileStorage _storage;
        private readonly INotificationService _notifications;
        private readonly ILogger<AssignmentService> _logger;

        public AssignmentService(IUnitOfWork unitOfWork, IFileStorage storage, INotificationService notifications, ILogger<AssignmentService> logger)
        {
            _unitOfWork = unitOfWork;
            _storage = storage;
            _notifications = notifications;
            _logger = logger;
        }

        // ---------------------------------------------------------------- staff & teachers

        public async Task<IReadOnlyList<AssignmentDto>> ListAsync(AssignmentActor actor, Guid? batchId, CancellationToken cancellationToken = default)
        {
            var query = await ScopedAsync(actor, cancellationToken);
            if (batchId is not null) query = query.Where(a => a.BatchId == batchId);
            var items = await query.OrderByDescending(a => a.DueAtUtc ?? a.CreatedAtUtc).Take(300).ToListAsync(cancellationToken);
            return items.Select(a => ToDto<AssignmentDto>(a)).ToList();
        }

        public async Task<IReadOnlyList<AssignableBatchDto>> ListBatchesAsync(AssignmentActor actor, CancellationToken cancellationToken = default)
        {
            var query = _unitOfWork.Repository<Batch>().Query().Where(b => b.Status == BatchStatus.Active);
            if (!actor.IsStaff)
            {
                var teacherId = await TeacherProfileIdAsync(actor.UserId, cancellationToken);
                query = query.Where(b => b.TeacherProfileId == teacherId);
            }
            return await query.OrderBy(b => b.Name).Select(b => new AssignableBatchDto
            {
                Id = b.Id,
                Name = b.Name,
                CourseName = b.Course.Name,
                StudentCount = b.Enrollments.Count(e => e.Status == EnrollmentStatus.Active),
            }).ToListAsync(cancellationToken);
        }

        public async Task<AssignmentDetailDto> GetAsync(AssignmentActor actor, Guid id, CancellationToken cancellationToken = default)
        {
            var assignment = await (await ScopedAsync(actor, cancellationToken)).FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                ?? throw new NotFoundException("Assignment not found.");
            return ToDetail(assignment);
        }

        public async Task<AssignmentDetailDto> CreateAsync(AssignmentActor actor, SaveAssignmentRequest request, UploadedFile? file, CancellationToken cancellationToken = default)
        {
            Validate(request);
            var batch = await _unitOfWork.Repository<Batch>().Query()
                .Include(b => b.Enrollments)
                .FirstOrDefaultAsync(b => b.Id == request.BatchId, cancellationToken)
                ?? throw new NotFoundException("Batch not found.");
            if (!actor.IsStaff && batch.TeacherProfileId != await TeacherProfileIdAsync(actor.UserId, cancellationToken))
                throw new ForbiddenException("You can only set work for your own batches.");

            var assignment = new Assignment
            {
                BatchId = batch.Id,
                TeacherProfileId = batch.TeacherProfileId,
                Kind = request.Kind,
                Title = request.Title.Trim(),
                Instructions = Clean(request.Instructions),
                Topic = Clean(request.Topic),
                DueAtUtc = request.DueAtUtc,
                MaxMarks = request.MaxMarks,
            };
            if (file is not null) (assignment.AttachmentPath, assignment.AttachmentFileName) = await StoreAsync(file, cancellationToken);

            var students = batch.Enrollments.Where(e => e.Status == EnrollmentStatus.Active).Select(e => e.ChildId).Distinct().ToList();
            foreach (var childId in students)
                assignment.Submissions.Add(new AssignmentSubmission { ChildId = childId });

            await _unitOfWork.Repository<Assignment>().AddAsync(assignment, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await NotifyFamiliesAsync(assignment.Id, students,
                request.Kind == AssignmentKind.Test ? NotificationType.General : NotificationType.Homework,
                request.Kind == AssignmentKind.Test ? $"New test: {assignment.Title}" : $"New homework: {assignment.Title}",
                child => assignment.DueAtUtc is null
                    ? $"{child} has new {(request.Kind == AssignmentKind.Test ? "test" : "homework")} from {batch.Name}."
                    : $"{child} has new {(request.Kind == AssignmentKind.Test ? "test" : "homework")} from {batch.Name}, due {assignment.DueAtUtc:dd MMM}.",
                cancellationToken);

            return await GetAsync(actor, assignment.Id, cancellationToken);
        }

        public async Task<AssignmentDetailDto> UpdateAsync(AssignmentActor actor, Guid id, SaveAssignmentRequest request, UploadedFile? file, CancellationToken cancellationToken = default)
        {
            Validate(request);
            var assignment = await (await ScopedAsync(actor, cancellationToken, tracked: true)).FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                ?? throw new NotFoundException("Assignment not found.");
            if (assignment.Kind != request.Kind && assignment.Submissions.Any(s => s.Status != AssignmentSubmissionStatus.Pending))
                throw new DomainValidationException("This work already has submissions or marks, so it can't change between homework and test.");

            assignment.Kind = request.Kind;
            assignment.Title = request.Title.Trim();
            assignment.Instructions = Clean(request.Instructions);
            assignment.Topic = Clean(request.Topic);
            assignment.DueAtUtc = request.DueAtUtc;
            assignment.MaxMarks = request.MaxMarks;
            if (file is not null) (assignment.AttachmentPath, assignment.AttachmentFileName) = await StoreAsync(file, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return await GetAsync(actor, id, cancellationToken);
        }

        public async Task DeleteAsync(AssignmentActor actor, Guid id, CancellationToken cancellationToken = default)
        {
            var assignment = await (await ScopedAsync(actor, cancellationToken, tracked: true)).FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                ?? throw new NotFoundException("Assignment not found.");
            foreach (var submission in assignment.Submissions) _unitOfWork.Repository<AssignmentSubmission>().Remove(submission);
            _unitOfWork.Repository<Assignment>().Remove(assignment);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task<AssignmentSubmissionDto> GradeAsync(AssignmentActor actor, Guid submissionId, GradeSubmissionRequest request, CancellationToken cancellationToken = default)
        {
            var submission = await _unitOfWork.Repository<AssignmentSubmission>().TrackedQuery()
                .Include(s => s.Assignment).ThenInclude(a => a.Batch)
                .Include(s => s.Child)
                .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken)
                ?? throw new NotFoundException("Submission not found.");
            await EnsureCanManageAsync(actor, submission.Assignment, cancellationToken);

            var assignment = submission.Assignment;
            if (request.Excused)
            {
                submission.Status = AssignmentSubmissionStatus.Excused;
                submission.Marks = null;
            }
            else
            {
                if (assignment.Kind == AssignmentKind.Test && request.Marks is null)
                    throw new DomainValidationException("Enter marks for a test.");
                if (request.Marks is not null && assignment.MaxMarks is not null && request.Marks > assignment.MaxMarks)
                    throw new DomainValidationException($"Marks can't be more than {assignment.MaxMarks:0.##}.");
                submission.Status = AssignmentSubmissionStatus.Graded;
                submission.Marks = request.Marks;
            }
            submission.Feedback = Clean(request.Feedback);
            submission.GradedAtUtc = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Homework feedback goes to the family straight away; test marks wait for the release.
            if (assignment.Kind == AssignmentKind.Homework && submission.Status == AssignmentSubmissionStatus.Graded)
            {
                await NotifyFamiliesAsync(assignment.Id, [submission.ChildId], NotificationType.Homework,
                    $"Homework checked: {assignment.Title}",
                    child => $"{child}'s homework has been checked{(submission.Marks is null ? "" : $" — {submission.Marks:0.##}/{assignment.MaxMarks:0.##}")}.",
                    cancellationToken);
            }

            return ToSubmissionDto(submission, assignment);
        }

        public async Task<AssignmentDetailDto> ReleaseResultsAsync(AssignmentActor actor, Guid id, CancellationToken cancellationToken = default)
        {
            var assignment = await (await ScopedAsync(actor, cancellationToken, tracked: true)).FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                ?? throw new NotFoundException("Assignment not found.");
            if (assignment.Kind != AssignmentKind.Test) throw new DomainValidationException("Only tests have results to release.");
            if (assignment.ResultsReleasedAtUtc is not null) return ToDetail(assignment);
            var pending = assignment.Submissions.Count(s => s.Status is AssignmentSubmissionStatus.Pending or AssignmentSubmissionStatus.Submitted);
            if (pending > 0) throw new DomainValidationException($"{pending} student{(pending == 1 ? " still needs" : "s still need")} marks or to be marked excused.");

            assignment.ResultsReleasedAtUtc = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var graded = assignment.Submissions.Where(s => s.Status == AssignmentSubmissionStatus.Graded).ToList();
            await NotifyFamiliesAsync(assignment.Id, graded.Select(s => s.ChildId).ToList(), NotificationType.TestResult,
                $"Test results: {assignment.Title}",
                child => $"{child}'s result for {assignment.Title} is ready.",
                cancellationToken);
            return ToDetail(assignment);
        }

        public async Task<AttachmentDownload> GetAttachmentForStaffAsync(AssignmentActor actor, Guid id, CancellationToken cancellationToken = default)
        {
            var assignment = await (await ScopedAsync(actor, cancellationToken)).FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                ?? throw new NotFoundException("Assignment not found.");
            return assignment.AttachmentPath is null ? throw new NotFoundException("No worksheet attached.")
                : new AttachmentDownload(assignment.AttachmentPath, assignment.AttachmentFileName ?? "worksheet");
        }

        public async Task<AttachmentDownload> GetSubmissionFileForStaffAsync(AssignmentActor actor, Guid submissionId, CancellationToken cancellationToken = default)
        {
            var submission = await _unitOfWork.Repository<AssignmentSubmission>().Query()
                .Include(s => s.Assignment).ThenInclude(a => a.Batch)
                .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken)
                ?? throw new NotFoundException("Submission not found.");
            await EnsureCanManageAsync(actor, submission.Assignment, cancellationToken);
            return submission.AttachmentPath is null ? throw new NotFoundException("No file was handed in.")
                : new AttachmentDownload(submission.AttachmentPath, submission.AttachmentFileName ?? "submission");
        }

        // ---------------------------------------------------------------- families

        public async Task<IReadOnlyList<FamilyAssignmentDto>> ListForParentAsync(Guid parentUserId, Guid? childId, CancellationToken cancellationToken = default)
        {
            var childIds = await ChildIdsAsync(parentUserId, cancellationToken);
            if (childId is not null)
            {
                if (!childIds.Contains(childId.Value)) throw new NotFoundException("Student not found.");
                childIds = [childId.Value];
            }

            var submissions = await FamilyQuery()
                .Where(s => childIds.Contains(s.ChildId))
                .OrderByDescending(s => s.Assignment.DueAtUtc ?? s.Assignment.CreatedAtUtc)
                .Take(200)
                .ToListAsync(cancellationToken);

            var releasedTestIds = submissions.Where(s => s.Assignment.Kind == AssignmentKind.Test && s.Assignment.ResultsReleasedAtUtc != null)
                .Select(s => s.AssignmentId).Distinct().ToList();
            var averages = releasedTestIds.Count == 0 ? new Dictionary<Guid, decimal?>() : await _unitOfWork.Repository<AssignmentSubmission>().Query()
                .Where(s => releasedTestIds.Contains(s.AssignmentId) && s.Status == AssignmentSubmissionStatus.Graded && s.Marks != null)
                .GroupBy(s => s.AssignmentId)
                .Select(g => new { g.Key, Average = g.Average(s => s.Marks) })
                .ToDictionaryAsync(x => x.Key, x => x.Average, cancellationToken);

            return submissions.Select(s => ToFamilyDto(s, averages.GetValueOrDefault(s.AssignmentId))).ToList();
        }

        public async Task<FamilyAssignmentDto> SubmitAsync(Guid parentUserId, Guid submissionId, SubmitAssignmentRequest request, UploadedFile? file, CancellationToken cancellationToken = default)
        {
            var childIds = await ChildIdsAsync(parentUserId, cancellationToken);
            var submission = await _unitOfWork.Repository<AssignmentSubmission>().TrackedQuery()
                .Include(s => s.Assignment)
                .FirstOrDefaultAsync(s => s.Id == submissionId && childIds.Contains(s.ChildId), cancellationToken)
                ?? throw new NotFoundException("Homework not found.");
            if (submission.Assignment.Kind != AssignmentKind.Homework)
                throw new DomainValidationException("Tests are marked by the teacher; there is nothing to hand in.");
            if (submission.Status is AssignmentSubmissionStatus.Graded or AssignmentSubmissionStatus.Excused)
                throw new DomainValidationException("This homework has already been checked.");
            var answer = Clean(request.AnswerText);
            if (answer is null && file is null && submission.AttachmentPath is null)
                throw new DomainValidationException("Add an answer or attach a photo of the work.");

            submission.AnswerText = answer ?? submission.AnswerText;
            if (file is not null) (submission.AttachmentPath, submission.AttachmentFileName) = await StoreAsync(file, cancellationToken);
            submission.Status = AssignmentSubmissionStatus.Submitted;
            submission.SubmittedAtUtc = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var fresh = await FamilyQuery().FirstAsync(s => s.Id == submissionId, cancellationToken);
            return ToFamilyDto(fresh, null);
        }

        public async Task<AttachmentDownload> GetAttachmentForParentAsync(Guid parentUserId, Guid submissionId, CancellationToken cancellationToken = default)
        {
            var submission = await OwnSubmissionAsync(parentUserId, submissionId, cancellationToken);
            return submission.Assignment.AttachmentPath is null ? throw new NotFoundException("No worksheet attached.")
                : new AttachmentDownload(submission.Assignment.AttachmentPath, submission.Assignment.AttachmentFileName ?? "worksheet");
        }

        public async Task<AttachmentDownload> GetSubmissionFileForParentAsync(Guid parentUserId, Guid submissionId, CancellationToken cancellationToken = default)
        {
            var submission = await OwnSubmissionAsync(parentUserId, submissionId, cancellationToken);
            return submission.AttachmentPath is null ? throw new NotFoundException("No file was handed in.")
                : new AttachmentDownload(submission.AttachmentPath, submission.AttachmentFileName ?? "submission");
        }

        // ---------------------------------------------------------------- helpers

        private async Task<IQueryable<Assignment>> ScopedAsync(AssignmentActor actor, CancellationToken cancellationToken, bool tracked = false)
        {
            var repository = _unitOfWork.Repository<Assignment>();
            IQueryable<Assignment> query = (tracked ? repository.TrackedQuery() : repository.Query())
                .Include(a => a.Batch).ThenInclude(b => b.Course)
                .Include(a => a.TeacherProfile).ThenInclude(t => t.User)
                .Include(a => a.Submissions).ThenInclude(s => s.Child);
            if (actor.IsStaff) return query;
            var teacherId = await TeacherProfileIdAsync(actor.UserId, cancellationToken);
            return query.Where(a => a.TeacherProfileId == teacherId || a.Batch.TeacherProfileId == teacherId);
        }

        private async Task EnsureCanManageAsync(AssignmentActor actor, Assignment assignment, CancellationToken cancellationToken)
        {
            if (actor.IsStaff) return;
            var teacherId = await TeacherProfileIdAsync(actor.UserId, cancellationToken);
            if (assignment.TeacherProfileId != teacherId && assignment.Batch.TeacherProfileId != teacherId)
                throw new NotFoundException("Submission not found.");
        }

        private async Task<Guid> TeacherProfileIdAsync(Guid userId, CancellationToken cancellationToken) =>
            await _unitOfWork.Repository<TeacherProfile>().Query()
                .Where(t => t.UserId == userId).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new ForbiddenException("Only teachers can manage homework.");

        private async Task<List<Guid>> ChildIdsAsync(Guid parentUserId, CancellationToken cancellationToken) =>
            await _unitOfWork.Repository<Child>().Query()
                .Where(c => c.ParentProfile.UserId == parentUserId).Select(c => c.Id).ToListAsync(cancellationToken);

        private IQueryable<AssignmentSubmission> FamilyQuery() =>
            _unitOfWork.Repository<AssignmentSubmission>().Query()
                .Include(s => s.Child)
                .Include(s => s.Assignment).ThenInclude(a => a.Batch).ThenInclude(b => b.Course)
                .Include(s => s.Assignment).ThenInclude(a => a.TeacherProfile).ThenInclude(t => t.User);

        private async Task<AssignmentSubmission> OwnSubmissionAsync(Guid parentUserId, Guid submissionId, CancellationToken cancellationToken)
        {
            var childIds = await ChildIdsAsync(parentUserId, cancellationToken);
            return await _unitOfWork.Repository<AssignmentSubmission>().Query()
                .Include(s => s.Assignment)
                .FirstOrDefaultAsync(s => s.Id == submissionId && childIds.Contains(s.ChildId), cancellationToken)
                ?? throw new NotFoundException("Homework not found.");
        }

        private async Task NotifyFamiliesAsync(Guid assignmentId, IReadOnlyCollection<Guid> childIds, NotificationType type,
            string subject, Func<string, string> body, CancellationToken cancellationToken)
        {
            if (childIds.Count == 0) return;
            var children = await _unitOfWork.Repository<Child>().Query()
                .Where(c => childIds.Contains(c.Id))
                .Select(c => new { c.FirstName, c.ParentProfile.UserId })
                .ToListAsync(cancellationToken);
            foreach (var child in children)
            {
                try
                {
                    await _notifications.NotifyInAppAsync(child.UserId, type, subject, body(child.FirstName),
                        NotificationTargets.Homework, assignmentId, cancellationToken);
                }
                catch (Exception ex)
                {
                    // A notification failure must not undo the teacher's work.
                    _logger.LogWarning(ex, "Homework notification failed for assignment {AssignmentId}", assignmentId);
                }
            }
        }

        private async Task<(string Path, string FileName)> StoreAsync(UploadedFile file, CancellationToken cancellationToken)
        {
            if (file.Length <= 0) throw new DomainValidationException("The file is empty.");
            if (file.Length > MaxFileBytes) throw new DomainValidationException("Files must be 15 MB or smaller.");
            var extension = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(extension))
                throw new DomainValidationException("Upload a PDF, photo (JPG/PNG/HEIC/WEBP), Word or text file.");
            var name = Path.GetFileName(file.FileName);
            if (name.Length > 200) name = name[^200..];
            var stored = await _storage.StoreAsync(file.Content, name, cancellationToken);
            return (stored.RelativePath, name);
        }

        private static void Validate(SaveAssignmentRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Title)) throw new DomainValidationException("Give the work a title.");
            if (request.Kind == AssignmentKind.Test && request.MaxMarks is null)
                throw new DomainValidationException("Set the maximum marks for a test.");
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string Name(Child child) => $"{child.FirstName} {child.LastName}".Trim();

        private static string? TeacherName(Assignment a) =>
            a.TeacherProfile?.User is { } u ? $"{u.FirstName} {u.LastName}".Trim() : null;

        private static T ToDto<T>(Assignment a) where T : AssignmentDto, new()
        {
            var graded = a.Submissions.Where(s => s.Status == AssignmentSubmissionStatus.Graded && s.Marks != null).ToList();
            return new T
            {
                Id = a.Id,
                BatchId = a.BatchId,
                BatchName = a.Batch.Name,
                CourseName = a.Batch.Course?.Name,
                TeacherName = TeacherName(a),
                Kind = a.Kind,
                Title = a.Title,
                Instructions = a.Instructions,
                Topic = a.Topic,
                AttachmentFileName = a.AttachmentFileName,
                DueAtUtc = a.DueAtUtc,
                MaxMarks = a.MaxMarks,
                ResultsReleasedAtUtc = a.ResultsReleasedAtUtc,
                CreatedAtUtc = a.CreatedAtUtc,
                StudentCount = a.Submissions.Count,
                SubmittedCount = a.Submissions.Count(s => s.Status is AssignmentSubmissionStatus.Submitted or AssignmentSubmissionStatus.Graded && s.SubmittedAtUtc != null),
                GradedCount = a.Submissions.Count(s => s.Status is AssignmentSubmissionStatus.Graded or AssignmentSubmissionStatus.Excused),
                AverageMarks = graded.Count == 0 ? null : Math.Round(graded.Average(s => s.Marks!.Value), 1),
            };
        }

        private static AssignmentDetailDto ToDetail(Assignment a)
        {
            var dto = ToDto<AssignmentDetailDto>(a);
            dto.Submissions = a.Submissions.OrderBy(s => s.Child.FirstName).Select(s => ToSubmissionDto(s, a)).ToList();
            return dto;
        }

        private static AssignmentSubmissionDto ToSubmissionDto(AssignmentSubmission s, Assignment a) => new()
        {
            Id = s.Id,
            ChildId = s.ChildId,
            ChildName = Name(s.Child),
            Status = s.Status,
            AnswerText = s.AnswerText,
            AttachmentFileName = s.AttachmentFileName,
            SubmittedAtUtc = s.SubmittedAtUtc,
            IsLate = s.SubmittedAtUtc is not null && a.DueAtUtc is not null && s.SubmittedAtUtc > a.DueAtUtc,
            Marks = s.Marks,
            Feedback = s.Feedback,
            GradedAtUtc = s.GradedAtUtc,
        };

        private static FamilyAssignmentDto ToFamilyDto(AssignmentSubmission s, decimal? classAverage)
        {
            var a = s.Assignment;
            var released = a.Kind == AssignmentKind.Homework || a.ResultsReleasedAtUtc != null;
            return new FamilyAssignmentDto
            {
                AssignmentId = a.Id,
                SubmissionId = s.Id,
                ChildId = s.ChildId,
                ChildName = Name(s.Child),
                BatchName = a.Batch.Name,
                CourseName = a.Batch.Course?.Name,
                TeacherName = TeacherName(a),
                Kind = a.Kind,
                Title = a.Title,
                Instructions = a.Instructions,
                Topic = a.Topic,
                AttachmentFileName = a.AttachmentFileName,
                DueAtUtc = a.DueAtUtc,
                MaxMarks = a.MaxMarks,
                // Before release a test shows as "awaiting results" whatever the teacher has entered so far.
                Status = !released && s.Status == AssignmentSubmissionStatus.Graded ? AssignmentSubmissionStatus.Pending : s.Status,
                AnswerText = s.AnswerText,
                SubmissionFileName = s.AttachmentFileName,
                SubmittedAtUtc = s.SubmittedAtUtc,
                IsOverdue = a.Kind == AssignmentKind.Homework && s.Status == AssignmentSubmissionStatus.Pending
                            && a.DueAtUtc is not null && a.DueAtUtc < DateTime.UtcNow,
                Marks = released ? s.Marks : null,
                Feedback = released ? s.Feedback : null,
                ResultsReleased = a.Kind == AssignmentKind.Test && a.ResultsReleasedAtUtc != null,
                ClassAverage = classAverage is null ? null : Math.Round(classAverage.Value, 1),
                CanSubmit = a.Kind == AssignmentKind.Homework && s.Status is AssignmentSubmissionStatus.Pending or AssignmentSubmissionStatus.Submitted,
            };
        }
    }
}
