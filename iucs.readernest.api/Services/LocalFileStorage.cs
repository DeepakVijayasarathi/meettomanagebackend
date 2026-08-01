using iucs.readernest.application.Common.Exceptions;
using iucs.readernest.application.Common.Interfaces;

namespace iucs.readernest.api.Services
{
    /// <summary>
    /// Local-disk implementation for development: files land under
    /// {ContentRoot}/uploads with a GUID name (original name preserved in metadata).
    /// Replace with client-owned cloud storage for production.
    /// </summary>
    public class LocalFileStorage : IFileStorage
    {
        // Learning-resource types only: worksheets/books/slides, images, audio/video,
        // zipped bundles. Deliberately excludes executables and script/markup types
        // (.exe/.sh/.js/.html/.svg/...) that could be uploaded under a resource's file
        // slot and later served back with a client-supplied Content-Type.
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".txt",
            ".jpg", ".jpeg", ".png", ".gif", ".webp",
            ".mp4", ".webm", ".mov", ".mp3", ".wav", ".zip",
        };

        private readonly string _rootPath;

        public LocalFileStorage(IWebHostEnvironment environment, IConfiguration configuration)
        {
            _rootPath = configuration["Storage:LocalPath"]
                ?? Path.Combine(environment.ContentRootPath, "uploads");
        }

        public async Task<StoredFile> StoreAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken = default)
        {
            var extension = Path.GetExtension(originalFileName);
            if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
            {
                throw new DomainValidationException(
                    $"File type '{extension}' is not allowed. Supported types: " +
                    string.Join(", ", AllowedExtensions.OrderBy(e => e)) + ".");
            }

            Directory.CreateDirectory(_rootPath);

            var relativePath = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            var absolutePath = Path.Combine(_rootPath, relativePath);

            await using var target = File.Create(absolutePath);
            await content.CopyToAsync(target, cancellationToken);

            return new StoredFile { RelativePath = relativePath, SizeBytes = target.Length };
        }

        public string GetAbsolutePath(string relativePath)
        {
            return Path.Combine(_rootPath, relativePath);
        }
    }
}
