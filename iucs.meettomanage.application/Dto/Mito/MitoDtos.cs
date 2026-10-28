using System.ComponentModel.DataAnnotations;

namespace iucs.meettomanage.application.Dto.Mito;

public class MitoChatTurn
{
    [Required, RegularExpression("user|assistant")]
    public string Role { get; set; } = "user";

    [Required, StringLength(2000, MinimumLength = 1)]
    public string Text { get; set; } = string.Empty;
}

public class MitoChatRequest
{
    [Required]
    public Guid ChildId { get; set; }

    [Required, MinLength(1), MaxLength(20)]
    public List<MitoChatTurn> Messages { get; set; } = [];

    [RegularExpression("doubt|plan|review|general")]
    public string Mode { get; set; } = "general";
}

public class MitoChatResponse
{
    public string Reply { get; set; } = string.Empty;
    public string Expression { get; set; } = "happy";
    public IReadOnlyList<string> SuggestedPrompts { get; set; } = [];
}

public class MitoTodayResponse
{
    public string StudentName { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
    public IReadOnlyList<MitoTodayItem> Items { get; set; } = [];
}

public class MitoTodayItem
{
    public Guid SessionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string TeacherName { get; set; } = string.Empty;
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class MitoProgressResponse
{
    public string StudentName { get; set; } = string.Empty;
    public string? AcademicLevel { get; set; }
    public int ClassesCompleted { get; set; }
    public int ClassesRemaining { get; set; }
    public double? AttendancePercent { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public class MitoQuizRequest
{
    [Required]
    public Guid ChildId { get; set; }

    [Range(1, 10)]
    public int Count { get; set; } = 5;
}

public class MitoQuizQuestionDto
{
    public Guid Id { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? CourseName { get; set; }
    public IReadOnlyList<MitoQuizOptionDto> Options { get; set; } = [];
}

public class MitoQuizOptionDto
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class MitoQuizAnswerRequest
{
    [Required]
    public Guid ChildId { get; set; }
    [Required]
    public Guid QuestionId { get; set; }
    [Required]
    public Guid OptionId { get; set; }
}

public class MitoQuizAnswerResponse
{
    public bool Correct { get; set; }
    public Guid CorrectOptionId { get; set; }
    public string CorrectAnswer { get; set; } = string.Empty;
    public string Feedback { get; set; } = string.Empty;
}

public record MitoStudentContext(
    string StudentName,
    string? AcademicLevel,
    int ClassesCompleted,
    int ClassesRemaining,
    double? AttendancePercent,
    IReadOnlyList<string> UpcomingClasses,
    IReadOnlyList<string> RecentClassSummaries);
