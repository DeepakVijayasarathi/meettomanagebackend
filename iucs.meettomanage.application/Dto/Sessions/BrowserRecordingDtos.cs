using System.ComponentModel.DataAnnotations;

namespace iucs.meettomanage.application.Dto.Sessions
{
    /// <summary>Starts uploading a recording made in the teacher's browser.</summary>
    public class StartBrowserRecordingRequest
    {
        /// <summary>The recorder's container type: video/mp4 or video/webm.</summary>
        [Required, MaxLength(100)]
        public string ContentType { get; set; } = null!;
    }

    public class BrowserRecordingUploadDto
    {
        public string Key { get; set; } = null!;

        public string UploadId { get; set; } = null!;

        /// <summary>Every part but the last must be exactly this size.</summary>
        public long PartSizeBytes { get; set; }
    }

    public class BrowserRecordingPartsRequest
    {
        [Required, MaxLength(200)]
        public string Key { get; set; } = null!;

        [Required, MaxLength(1000)]
        public string UploadId { get; set; } = null!;

        [Required, MinLength(1), MaxLength(20)]
        public List<int> PartNumbers { get; set; } = [];
    }

    public class BrowserRecordingPartUrlDto
    {
        public int PartNumber { get; set; }

        public string Url { get; set; } = null!;
    }

    public class BrowserRecordingPart
    {
        public int PartNumber { get; set; }

        [Required, MaxLength(200)]
        public string ETag { get; set; } = null!;
    }

    public class CompleteBrowserRecordingRequest
    {
        [Required, MaxLength(200)]
        public string Key { get; set; } = null!;

        [Required, MaxLength(1000)]
        public string UploadId { get; set; } = null!;

        [Required, MinLength(1)]
        public List<BrowserRecordingPart> Parts { get; set; } = [];

        [Range(0, 24 * 3600)]
        public int? DurationSeconds { get; set; }
    }

    public class AbortBrowserRecordingRequest
    {
        [Required, MaxLength(200)]
        public string Key { get; set; } = null!;

        [Required, MaxLength(1000)]
        public string UploadId { get; set; } = null!;
    }
}
