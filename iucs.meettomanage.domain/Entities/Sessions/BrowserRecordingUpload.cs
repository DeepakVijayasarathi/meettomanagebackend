using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Sessions
{
    /// <summary>
    /// A class recording being uploaded from the teacher's browser, part by part, while the class
    /// runs. Tracked so (a) only an upload started for a class can be attached to it, and (b) an
    /// upload whose browser crashed or closed is still finished from the parts already stored
    /// (BrowserRecordingRecoveryBackgroundService), even if the teacher never comes back.
    /// </summary>
    [Index(nameof(StorageKey), IsUnique = true)]
    [Index(nameof(CompletedAtUtc), nameof(LastActivityAtUtc))]
    public class BrowserRecordingUpload : BaseEntity
    {
        public Guid ClassSessionId { get; set; }

        public ClassSession ClassSession { get; set; } = null!;

        [MaxLength(200)]
        public string StorageKey { get; set; } = null!;

        [MaxLength(1000)]
        public string UploadId { get; set; } = null!;

        /// <summary>When a part was last requested: the recorder asks for one about every minute.</summary>
        public DateTime LastActivityAtUtc { get; set; }

        public DateTime? CompletedAtUtc { get; set; }

        /// <summary>Who started it, so a recovered recording is filed the same way the teacher's own would be.</summary>
        public Guid? StartedByUserId { get; set; }
    }
}
