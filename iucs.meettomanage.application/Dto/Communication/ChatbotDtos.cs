namespace iucs.meettomanage.application.Dto.Communication
{
    public class AiChatConfigurationDto
    {
        public bool Enabled { get; set; }
        public string Endpoint { get; set; } = string.Empty;
        public string Model { get; set; } = "Qwen/Qwen3.6-35B-A3B-FP8";
        public string SystemPrompt { get; set; } = "Answer in 2-3 short sentences.";
        public int MaxTokens { get; set; } = 500;
        public double Temperature { get; set; } = 0.3;
        public bool HasApiKey { get; set; }
        public string MaskedApiKey { get; set; } = string.Empty;
        public decimal InputCostPerMillionTokensUsd { get; set; }
        public decimal OutputCostPerMillionTokensUsd { get; set; }
        public decimal MonthlyBudgetUsd { get; set; }
    }

    public class UpdateAiChatConfigurationRequest
    {
        public bool Enabled { get; set; }
        public string Endpoint { get; set; } = string.Empty;
        public string Model { get; set; } = "Qwen/Qwen3.6-35B-A3B-FP8";
        public string SystemPrompt { get; set; } = "Answer in 2-3 short sentences.";
        public int MaxTokens { get; set; } = 500;
        public double Temperature { get; set; } = 0.3;
        /// <summary>Null/blank preserves the current key; a new value rotates it.</summary>
        public string? ApiKey { get; set; }
        public bool ClearApiKey { get; set; }
        public decimal InputCostPerMillionTokensUsd { get; set; }
        public decimal OutputCostPerMillionTokensUsd { get; set; }
        public decimal MonthlyBudgetUsd { get; set; }
    }

    public class TestAiChatRequest
    {
        public string Message { get; set; } = "What is Node.js?";
    }

    public class TestAiChatResponse
    {
        public string Answer { get; set; } = string.Empty;
    }

    public class AiModelListDto
    {
        public IReadOnlyList<string> Models { get; set; } = [];
    }

    public class AiCostMonitorDto
    {
        public string PeriodLabel { get; set; } = string.Empty;
        public long Requests { get; set; }
        public long SuccessfulRequests { get; set; }
        public long FailedRequests { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens => InputTokens + OutputTokens;
        public decimal EstimatedCostUsd { get; set; }
        public decimal MonthlyBudgetUsd { get; set; }
        public double BudgetUsedPercent { get; set; }
        public bool PricingConfigured { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }

    public class BusinessAiBriefDto
    {
        public string ExecutiveSummary { get; set; } = string.Empty;
        public bool AiPowered { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public BusinessCostSnapshotDto CostSnapshot { get; set; } = new();
        public IReadOnlyList<BusinessStudentInsightDto> StudentProgressInsights { get; set; } = [];
        public IReadOnlyList<BusinessClassSummaryDto> RecentClassSummaries { get; set; } = [];
    }

    public class BusinessCostSnapshotDto
    {
        public string PeriodLabel { get; set; } = string.Empty;
        public decimal CollectedRevenue { get; set; }
        public decimal OutstandingRevenue { get; set; }
        public decimal OverdueRevenue { get; set; }
        public int OverdueInvoiceCount { get; set; }
        public decimal TeacherPayoutCost { get; set; }
        public decimal StaffPayrollCost { get; set; }
        public decimal TotalPeopleCost { get; set; }
        public decimal EstimatedContribution { get; set; }
        public double? EstimatedMarginPercent { get; set; }
        public decimal ProjectedMonthRevenue { get; set; }
        public decimal ProjectedMonthContribution { get; set; }
        public string ForecastBasis { get; set; } = string.Empty;
        public decimal? CostPerActiveEnrollment { get; set; }
        public int ActiveEnrollmentCount { get; set; }
        public int EligibleStaffCount { get; set; }
        public int ConfiguredStaffCount { get; set; }
        public bool HasRecordedPeopleCosts { get; set; }
        public string CoverageNote { get; set; } = string.Empty;
    }

    public class BusinessStudentInsightDto
    {
        public Guid ChildId { get; set; }
        public string ChildName { get; set; } = string.Empty;
        public double? AttendancePercent { get; set; }
        public int EngagementScore { get; set; }
        public string Priority { get; set; } = "Monitor";
        public string Insight { get; set; } = string.Empty;
    }

    public class BusinessClassSummaryDto
    {
        public Guid SessionId { get; set; }
        public string ClassName { get; set; } = string.Empty;
        public string TeacherName { get; set; } = string.Empty;
        public DateTime CompletedAtUtc { get; set; }
        public string Summary { get; set; } = string.Empty;
    }

    public class BusinessCopilotRequest
    {
        public string Question { get; set; } = string.Empty;
        public IReadOnlyList<BusinessCopilotMessageDto> History { get; set; } = [];
    }

    public class BusinessCopilotMessageDto
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    public class BusinessCopilotResponse
    {
        public string Answer { get; set; } = string.Empty;
        public bool AiPowered { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }
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

        /// <summary>Null until rated; only meaningful on a Bot message that matched an FAQ.</summary>
        public bool? WasHelpful { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    public class AskChatbotRequest
    {
        public string Message { get; set; } = string.Empty;
    }

    public class SubmitChatFeedbackRequest
    {
        public bool Helpful { get; set; }

        /// <summary>
        /// The question that produced this answer — the service has no other way to recover it
        /// for the escalation it creates on negative feedback, since a ChatMessage row only
        /// stores its own turn, not the one before it. The client always has this already (it's
        /// the immediately preceding turn in the conversation it's rendering).
        /// </summary>
        public string OriginalQuestion { get; set; } = string.Empty;
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

        /// <summary>Bot answers a user explicitly marked unhelpful — a matched FAQ isn't
        /// necessarily a good answer, so this tracks quality separately from match rate.</summary>
        public int MarkedUnhelpful { get; set; }

        public IReadOnlyList<string> TopUnansweredQuestions { get; set; } = Array.Empty<string>();
    }
}
