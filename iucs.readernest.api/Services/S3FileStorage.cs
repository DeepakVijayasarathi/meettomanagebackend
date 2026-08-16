using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using iucs.readernest.application.Common.Exceptions;
using iucs.readernest.application.Common.Interfaces;

namespace iucs.readernest.api.Services
{
    /// <summary>
    /// S3-compatible object storage (Hetzner Object Storage, or any other S3-API provider —
    /// selected purely by ServiceURL). Chosen over the local-disk LocalFileStorage so uploaded
    /// resources survive a redeploy without depending on a correctly-mapped Docker volume,
    /// which local disk storage requires and which is easy to misconfigure.
    /// </summary>
    public class S3FileStorage : IFileStorage
    {
        // Same allowlist as LocalFileStorage — learning-resource types only, deliberately
        // excluding executables and script/markup types that could be served back with a
        // client-supplied Content-Type.
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".txt",
            ".jpg", ".jpeg", ".png", ".gif", ".webp",
            ".mp4", ".webm", ".mov", ".mp3", ".wav", ".zip",
        };

        private readonly IAmazonS3 _client;
        private readonly string _bucket;

        public S3FileStorage(IConfiguration configuration)
        {
            var endpoint = configuration["Storage:S3:Endpoint"]
                ?? throw new InvalidOperationException("Storage:S3:Endpoint is not configured.");
            var accessKey = configuration["Storage:S3:AccessKey"]
                ?? throw new InvalidOperationException("Storage:S3:AccessKey is not configured.");
            var secretKey = configuration["Storage:S3:SecretKey"]
                ?? throw new InvalidOperationException("Storage:S3:SecretKey is not configured.");
            _bucket = configuration["Storage:S3:BucketName"]
                ?? throw new InvalidOperationException("Storage:S3:BucketName is not configured.");

            var config = new AmazonS3Config
            {
                ServiceURL = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : $"https://{endpoint}",
                // Hetzner (and most non-AWS S3-compatible providers) serve buckets as
                // bucket.endpoint rather than AWS's endpoint/bucket path style.
                ForcePathStyle = false,
            };
            _client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
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

            var key = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";

            // S3's PutObject needs the content length up front and a seekable stream for
            // retries; the incoming upload stream (an ASP.NET Core IFormFile stream) is
            // neither guaranteed to be, so it's buffered to a MemoryStream first. Upload
            // sizes are already capped well below memory-pressure territory by
            // ResourcesController's RequestSizeLimit.
            await using var buffered = new MemoryStream();
            await content.CopyToAsync(buffered, cancellationToken);
            buffered.Position = 0;

            await _client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    InputStream = buffered,
                    AutoCloseStream = false,
                },
                cancellationToken);

            return new StoredFile { RelativePath = key, SizeBytes = buffered.Length };
        }

        public async Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _client.GetObjectAsync(_bucket, relativePath, cancellationToken);
                return response.ResponseStream;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }
    }
}
