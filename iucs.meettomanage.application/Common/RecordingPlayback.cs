using iucs.meettomanage.application.Common.Interfaces;

namespace iucs.meettomanage.application.Common
{
    /// <summary>
    /// Turns a stored recording reference into something a video player can open. Server (Jibri)
    /// recordings are registered with a full web address and pass through unchanged; recordings
    /// made in the teacher's browser are uploaded straight into the app's own object storage and
    /// stored by key, which is served through a short-lived signed address instead.
    /// </summary>
    public static class RecordingPlayback
    {
        private static readonly TimeSpan ValidFor = TimeSpan.FromHours(6);

        public static string Url(IDirectUploadStorage? storage, string storageUrl)
        {
            if (storage is null || string.IsNullOrWhiteSpace(storageUrl) || IsWebAddress(storageUrl))
            {
                return storageUrl;
            }

            try
            {
                return storage.GetReadUrl(storageUrl, ValidFor, ContentTypeFor(storageUrl));
            }
            catch (Exception)
            {
                // Not one of this app's stored objects (or storage unavailable): leave it as registered.
                return storageUrl;
            }
        }

        public static bool IsWebAddress(string value) =>
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        public static string? ContentTypeFor(string key) =>
            Path.GetExtension(key).ToLowerInvariant() switch
            {
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                _ => null,
            };
    }
}
