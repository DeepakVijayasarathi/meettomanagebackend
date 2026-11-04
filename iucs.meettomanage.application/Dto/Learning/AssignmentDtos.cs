using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Dto.Learning
{
    public class AssignmentDto
    {
        public Guid Id { get; set; }
        public Guid BatchId { get; set; }
        public string BatchName { get; set; } = null!;
        public string? CourseName { get; set; }
        public string? TeacherName { get; set; }
        public AssignmentKind Kind { get; set; }
        public string Title { get; set; } = null!;
        public string? Instructions { get; set; }
        public string? Topic { get; set; }
        public string? AttachmentFileName { get; set; }
        public DateTime? DueAtUtc { get; set; }
        public decimal? MaxMarks { get; set; }
        public DateTime? ResultsReleasedAtUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public int StudentCount { get; set; }
        public int SubmittedCount { get; set; }
        public int GradedCount { get; set; }
        /// <summary>Average marks across graded students, once any are graded.</summary>
        public decimal? AverageMarks { get; set; }
    }

    public class AssignmentDetailDto : AssignmentDto
    {
        public IReadOnlyList<AssignmentSubmissionDto> Submissions { get; set; } = [];
    }

    public class AssignmentSubmissionDto
    {
        public Guid Id { get; set; }
        public Guid ChildId { get; set; }
        public string ChildName { get; set; } = null!;
        public AssignmentSubmissionStatus Status { get; set; }
        public string? AnswerText { get; set; }
        public string? AttachmentFileName { get; set; }
        public DateTime? SubmittedAtUtc { get; set; }
        public bool IsLate { get; set; }
        public decimal? Marks { get; set; }
        public string? Feedback { get; set; }
        public DateTime? GradedAtUtc { get; set; }
    }

    /// <summary>One child's view of an assignment, for the parent portal, family app and Mito.</summary>
    public class FamilyAssignmentDto
    {
        public Guid AssignmentId { get; set; }
        public Guid SubmissionId { get; set; }
        public Guid ChildId { get; set; }
        public string ChildName { get; set; } = null!;
        public string BatchName { get; set; } = null!;
        public string? CourseName { get; set; }
        public string? TeacherName { get; set; }
        public AssignmentKind Kind { get; set; }
        public string Title { get; set; } = null!;
        public string? Instructions { get; set; }
        public string? Topic { get; set; }
        public string? AttachmentFileName { get; set; }
        public DateTime? DueAtUtc { get; set; }
        public decimal? MaxMarks { get; set; }
        public AssignmentSubmissionStatus Status { get; set; }
        public string? AnswerText { get; set; }
        public string? SubmissionFileName { get; set; }
        public DateTime? SubmittedAtUtc { get; set; }
        public bool IsOverdue { get; set; }
        /// <summary>Hidden for tests until results are released.</summary>
        public decimal? Marks { get; set; }
        public string? Feedback { get; set; }
        public bool ResultsReleased { get; set; }
        public decimal? ClassAverage { get; set; }
        /// <summary>Whether the family can still hand in (or replace) work: homework not yet graded or excused.</summary>
        public bool CanSubmit { get; set; }
    }

    public class SaveAssignmentRequest
    {
        public Guid BatchId { get; set; }

        public AssignmentKind Kind { get; set; }

        [Required, MaxLength(200)]
        public string Title { get; set; } = null!;

        [MaxLength(4000)]
        public string? Instructions { get; set; }

        [MaxLength(120)]
        public string? Topic { get; set; }

        public DateTime? DueAtUtc { get; set; }

        [Range(0.5, 10000)]
        public decimal? MaxMarks { get; set; }
    }

    public class GradeSubmissionRequest
    {
        [Range(0, 10000)]
        public decimal? Marks { get; set; }

        [MaxLength(2000)]
        public string? Feedback { get; set; }

        /// <summary>Marks the student as excused (e.g. absent with leave) instead of grading.</summary>
        public bool Excused { get; set; }
    }

    public class SubmitAssignmentRequest
    {
        [MaxLength(4000)]
        public string? AnswerText { get; set; }
    }

    /// <summary>An uploaded file handed from the API layer to the service.</summary>
    public record UploadedFile(Stream Content, string FileName, long Length);

    /// <summary>Who is acting: staff (Admin / permitted Sub Admin) see every batch; a teacher only their own.</summary>
    public record AssignmentActor(Guid UserId, bool IsStaff);

    public record AttachmentDownload(string Path, string FileName);
}
