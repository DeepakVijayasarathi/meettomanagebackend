using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Users
{
    /// <summary>
    /// A single-use, time-limited bearer token emailed to a user who requested a self-service
    /// PIN reset. System-generated (BaseEntity, no acting-user audit trail). The token itself
    /// is a 256-bit random value — unguessable on its own, and only useful for the short window
    /// before <see cref="ExpiresAtUtc"/> via the email channel the account owner controls, so it
    /// is stored as-is rather than hashed (unlike the PIN itself, which never leaves as plaintext).
    /// </summary>
    [Index(nameof(Token), IsUnique = true)]
    public class PinResetToken : BaseEntity
    {
        public Guid UserId { get; set; }

        public User User { get; set; } = null!;

        [MaxLength(64)]
        public string Token { get; set; } = null!;

        public DateTime ExpiresAtUtc { get; set; }

        public DateTime? UsedAtUtc { get; set; }
    }
}
