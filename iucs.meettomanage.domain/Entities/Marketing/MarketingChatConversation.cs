using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Marketing
{
    /// <summary>
    /// One visitor's conversation with the public website's AI chat assistant, kept so the
    /// marketing team can read what prospects ask. ConversationKey is a random id the browser
    /// generates per chat session. Removed with the other marketing data after the marketing
    /// report settings' retention period.
    /// </summary>
    [Index(nameof(ConversationKey), IsUnique = true)]
    [Index(nameof(LastMessageAtUtc))]
    public class MarketingChatConversation : BaseEntity
    {
        [MaxLength(64)]
        public string ConversationKey { get; set; } = null!;

        [MaxLength(100)]
        public string? VisitorId { get; set; }

        [MaxLength(100)]
        public string? Source { get; set; }

        [MaxLength(100)]
        public string? Medium { get; set; }

        [MaxLength(100)]
        public string? Campaign { get; set; }

        [MaxLength(200)]
        public string? LandingPath { get; set; }

        /// <summary>The visitor's first message, for the conversation list.</summary>
        [MaxLength(300)]
        public string FirstMessage { get; set; } = "";

        public int MessageCount { get; set; }

        public DateTime LastMessageAtUtc { get; set; }

        /// <summary>Set when the assistant booked a platform demo in this conversation.</summary>
        [MaxLength(150)]
        public string? BookedName { get; set; }

        public List<MarketingChatMessage> Messages { get; set; } = [];
    }

    [Index(nameof(ConversationId), nameof(Position))]
    public class MarketingChatMessage : BaseEntity
    {
        public Guid ConversationId { get; set; }

        /// <summary>0-based order within the conversation (several turns share one save, so CreatedAtUtc can tie).</summary>
        public int Position { get; set; }

        public MarketingChatConversation Conversation { get; set; } = null!;

        /// <summary>"user" (the visitor) or "assistant".</summary>
        [MaxLength(16)]
        public string Role { get; set; } = "user";

        [MaxLength(4000)]
        public string Text { get; set; } = "";
    }
}
