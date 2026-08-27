namespace iucs.readernest.application.Dto.Communication
{
    public class ChatFaqDto
    {
        public Guid Id { get; set; }

        public string Question { get; set; } = string.Empty;

        public string Answer { get; set; } = string.Empty;

        public string? Keywords { get; set; }

        public string? Category { get; set; }

        public bool IsActive { get; set; }

        public int SortOrder { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public DateTime? UpdatedAtUtc { get; set; }
    }

    public class SaveChatFaqRequest
    {
        public string Question { get; set; } = string.Empty;

        public string Answer { get; set; } = string.Empty;

        public string? Keywords { get; set; }

        public string? Category { get; set; }

        public bool IsActive { get; set; } = true;

        public int SortOrder { get; set; }
    }

    public class ChatMessageDto
    {
        public Guid Id { get; set; }

        /// <summary>"User" or "Bot".</summary>
        public string Sender { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public Guid? MatchedFaqId { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    public class AskChatbotRequest
    {
        public string Message { get; set; } = string.Empty;
    }

    public class AskChatbotResponse
    {
        public ChatMessageDto UserMessage { get; set; } = null!;

        public ChatMessageDto BotMessage { get; set; } = null!;

        public bool Matched { get; set; }

        public bool Escalated { get; set; }
    }

    public class ChatEscalationDto
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }

        public string UserName { get; set; } = string.Empty;

        /// <summary>"Pending" or "Resolved".</summary>
        public string Status { get; set; } = string.Empty;

        public string Question { get; set; } = string.Empty;

        public string? ResolutionNote { get; set; }

        public Guid? ResolvedByUserId { get; set; }

        public string? ResolvedByName { get; set; }

        public DateTime? ResolvedAtUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    public class ResolveChatEscalationRequest
    {
        public string ResolutionNote { get; set; } = string.Empty;
    }

    public class ChatbotUsageStatsDto
    {
        public int TotalQuestions { get; set; }

        public int AnsweredByBot { get; set; }

        public int EscalatedToTeacher { get; set; }

        public int PendingEscalations { get; set; }

        public int ActiveUsers { get; set; }

        public IReadOnlyList<string> TopUnansweredQuestions { get; set; } = Array.Empty<string>();
    }
}
