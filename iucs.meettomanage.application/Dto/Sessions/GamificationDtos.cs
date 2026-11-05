using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Dto.Sessions
{
    public class GrantAwardRequest
    {
        public Guid? SessionId { get; set; }

        [Required]
        [MaxLength(200)]
        public string ParticipantName { get; set; } = null!;

        public AwardKind Kind { get; set; } = AwardKind.Star;

        [MaxLength(100)]
        public string? Label { get; set; }

        [Range(1, 100)]
        public int Points { get; set; } = 1;
    }

    public class AwardDto
    {
        public Guid Id { get; set; }

        public Guid? SessionId { get; set; }

        public string ParticipantName { get; set; } = null!;

        public AwardKind Kind { get; set; }

        public string? Label { get; set; }

        public int Points { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>Aggregated leaderboard row: total stars plus earned badges/milestones.</summary>
    public class LeaderboardEntryDto
    {
        public string ParticipantName { get; set; } = null!;

        public int Stars { get; set; }

        public IReadOnlyList<string> Badges { get; set; } = [];
    }

    /// <summary>A badge or milestone a child earned, for the family app's rewards screen.</summary>
    public class ChildRewardDto
    {
        public string Label { get; set; } = null!;

        public AwardKind Kind { get; set; }

        public DateTime EarnedAtUtc { get; set; }
    }

    /// <summary>One child's all-time stars and earned badges. Never includes other children.</summary>
    public class ChildRewardsDto
    {
        public Guid ChildId { get; set; }

        public string ChildName { get; set; } = null!;

        public int TotalStars { get; set; }

        public int StarsLast30Days { get; set; }

        /// <summary>Badges and milestones, newest first.</summary>
        public IReadOnlyList<ChildRewardDto> Rewards { get; set; } = [];

        /// <summary>Next milestone's star target and label, or null once every milestone is earned.</summary>
        public int? NextMilestoneStars { get; set; }

        public string? NextMilestoneLabel { get; set; }
    }
}
