using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Marketing
{
    /// <summary>Where a prospect stands in cold outreach. Anything past Contacted stops further e-mails.</summary>
    public enum OutreachProspectStatus
    {
        New,
        Contacted,
        Replied,
        Interested,
        NotInterested,
        Unsubscribed,
        Bounced,
    }

    public enum OutreachCampaignStatus
    {
        Draft,
        Active,
        Paused,
        Completed,
    }

    public enum OutreachRecipientStatus
    {
        /// <summary>Waiting for its next step's send time.</summary>
        Scheduled,
        /// <summary>Every step was sent.</summary>
        Completed,
        /// <summary>The prospect replied, unsubscribed, bounced or was marked not interested.</summary>
        Stopped,
    }

    public enum OutreachPipelineStage { New, Qualified, Contacted, Replied, DemoBooked, Trial, Negotiation, Won, Lost }
    public enum OutreachActivityType { Imported, Note, EmailSent, ReplyReceived, StatusChanged, TaskCreated, TaskCompleted, AiQualified, ReplyHandled }

    /// <summary>
    /// A person the marketing team may e-mail about the platform (an academy owner, typically). One row per e-mail
    /// address; an unsubscribe is permanent and is honoured by every campaign.
    /// </summary>
    [Index(nameof(Email), IsUnique = true)]
    [Index(nameof(UnsubscribeToken), IsUnique = true)]
    [Index(nameof(Status))]
    public class OutreachProspect : BaseEntity
    {
        [MaxLength(256)] public string Email { get; set; } = null!;
        [MaxLength(120)] public string? FirstName { get; set; }
        [MaxLength(200)] public string? AcademyName { get; set; }
        [MaxLength(120)] public string? City { get; set; }
        [MaxLength(120)] public string? Region { get; set; }
        [MaxLength(40)] public string? Phone { get; set; }
        /// <summary>Where the contact came from (e.g. "Justdial", "event", "referral"), kept for consent questions.</summary>
        [MaxLength(120)] public string? Source { get; set; }
        [MaxLength(1000)] public string? Notes { get; set; }
        public int LeadScore { get; set; }
        [MaxLength(500)] public string? ScoreReason { get; set; }
        public OutreachPipelineStage PipelineStage { get; set; } = OutreachPipelineStage.New;
        [MaxLength(120)] public string? AssignedTo { get; set; }
        public DateTime? NextActionAtUtc { get; set; }
        public OutreachProspectStatus Status { get; set; } = OutreachProspectStatus.New;
        /// <summary>Unguessable id in the unsubscribe link.</summary>
        public Guid UnsubscribeToken { get; set; } = Guid.NewGuid();
        public DateTime? LastContactedAtUtc { get; set; }
        public DateTime? UnsubscribedAtUtc { get; set; }
    }

    /// <summary>A sequence of e-mails (a first message and up to two follow-ups) sent to the prospects enrolled in it.</summary>
    public class OutreachCampaign : BaseEntity
    {
        [MaxLength(150)] public string Name { get; set; } = null!;
        public OutreachCampaignStatus Status { get; set; } = OutreachCampaignStatus.Draft;
        public List<OutreachCampaignStep> Steps { get; set; } = [];
        public List<OutreachCampaignRecipient> Recipients { get; set; } = [];
    }

    /// <summary>One e-mail of a campaign. Step 1 goes out first; each later step waits DelayDays after the previous one.</summary>
    [Index(nameof(CampaignId), nameof(Order), IsUnique = true)]
    public class OutreachCampaignStep : BaseEntity
    {
        public Guid CampaignId { get; set; }
        public OutreachCampaign Campaign { get; set; } = null!;
        public int Order { get; set; }
        public int DelayDays { get; set; }
        [MaxLength(200)] public string Subject { get; set; } = null!;
        [MaxLength(10000)] public string Body { get; set; } = null!;
    }

    /// <summary>A prospect enrolled in a campaign, with the next step to send and when.</summary>
    [Index(nameof(CampaignId), nameof(ProspectId), IsUnique = true)]
    [Index(nameof(Status), nameof(NextSendAtUtc))]
    public class OutreachCampaignRecipient : BaseEntity
    {
        public Guid CampaignId { get; set; }
        public OutreachCampaign Campaign { get; set; } = null!;
        public Guid ProspectId { get; set; }
        public OutreachProspect Prospect { get; set; } = null!;
        /// <summary>Order of the next step to send (1-based).</summary>
        public int NextStep { get; set; } = 1;
        public DateTime NextSendAtUtc { get; set; } = DateTime.UtcNow;
        public OutreachRecipientStatus Status { get; set; } = OutreachRecipientStatus.Scheduled;
    }

    /// <summary>One e-mail actually sent (or attempted), for the daily limit and the campaign's history.</summary>
    [Index(nameof(SentAtUtc))]
    [Index(nameof(CampaignId))]
    public class OutreachSend : BaseEntity
    {
        public Guid CampaignId { get; set; }
        public Guid ProspectId { get; set; }
        public int StepOrder { get; set; }
        [MaxLength(256)] public string Email { get; set; } = null!;
        [MaxLength(200)] public string Subject { get; set; } = null!;
        public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
        public bool Succeeded { get; set; }
        [MaxLength(500)] public string? Error { get; set; }
    }

    [Index(nameof(ProspectId), nameof(CreatedAtUtc))]
    public class OutreachProspectActivity : BaseEntity
    {
        public Guid ProspectId { get; set; }
        public OutreachProspect Prospect { get; set; } = null!;
        public OutreachActivityType Type { get; set; }
        [MaxLength(200)] public string? Subject { get; set; }
        [MaxLength(10000)] public string? Detail { get; set; }
        [MaxLength(120)] public string? CreatedBy { get; set; }
        [MaxLength(200)] public string? ExternalId { get; set; }
    }

    [Index(nameof(ProspectId), nameof(DueAtUtc))]
    public class OutreachTask : BaseEntity
    {
        public Guid ProspectId { get; set; }
        public OutreachProspect Prospect { get; set; } = null!;
        [MaxLength(200)] public string Title { get; set; } = null!;
        [MaxLength(120)] public string? AssignedTo { get; set; }
        public DateTime DueAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
    }
}
