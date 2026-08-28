namespace iucs.meettomanage.application.Common.Interfaces
{
    public class StoredFile
    {
        public string RelativePath { get; set; } = null!;

        public long SizeBytes { get; set; }
    }

    /// <summary>
    /// Blob storage for uploaded learning content. LocalFileStorage (disk) and S3FileStorage
    /// (Hetzner Object Storage, S3-compatible) both implement this.
    /// </summary>
    public interface IFileStorage
    {
        Task<StoredFile> StoreAsync(Stream content, string originalFileName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Opens the stored object for reading, or null if it doesn't exist. Deliberately a
        /// stream rather than a resolved local path — a disk-backed implementation can return
        /// one trivially, but a remote object store has no local path to resolve to, and every
        /// caller needs to work with either. Callers must dispose the returned stream.
        /// </summary>
        Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);
    }
}
