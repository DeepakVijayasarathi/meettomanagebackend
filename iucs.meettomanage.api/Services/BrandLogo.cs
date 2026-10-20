using System.Security.Cryptography;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Settings;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.api.Services
{
    /// <summary>
    /// A client's uploaded logo. The bytes go to the tenant's own file storage (a setting value is capped at
    /// 2000 characters, far too small for an image); two settings point at it:
    /// brand.logoFile (private: "relativePath|contentType") and brand.logoUrl (public: the stable URL that
    /// <c>GET /api/settings/brand-logo</c> serves, with a ?v= version so a new upload busts caches).
    /// </summary>
    public static class BrandLogo
    {
        public const long MaxBytes = 1_000_000;
        public const string FileKey = "brand.logoFile";
        public const string UrlKey = "brand.logoUrl";

        // SVG is deliberately not offered: the shared file storage does not accept .svg, and SVG is markup that can carry script.
        private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

        /// <summary>Content type from the file's real bytes (never the client-supplied type); null when it is not an allowed image.</summary>
        public static string? Detect(byte[] bytes, string fileName)
        {
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!Extensions.Contains(extension)) return null;
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
            if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
                && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') return "image/webp";
            return null;
        }

        public static async Task<string> SaveAsync(IFileStorage storage, ISettingsService settings, Stream content, string fileName,
            string publicBaseUrl, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0) throw new DomainValidationException("The uploaded file is empty.");
            if (bytes.Length > MaxBytes) throw new DomainValidationException("The logo must be 1 MB or smaller.");
            var contentType = Detect(bytes, fileName)
                ?? throw new DomainValidationException("Upload a PNG, JPG or WebP image.");

            var extension = contentType switch { "image/png" => ".png", "image/jpeg" => ".jpg", _ => ".webp" };
            buffer.Position = 0;
            var stored = await storage.StoreAsync(buffer, "brand-logo" + extension, ct);
            var version = Convert.ToHexString(SHA256.HashData(bytes))[..10].ToLowerInvariant();
            var url = $"{publicBaseUrl}/api/settings/brand-logo?v={version}";
            await settings.UpsertAsync(
            [
                new UpdateSettingRequest { Key = FileKey, Value = $"{stored.RelativePath}|{contentType}", Category = SettingCategory.Branding, IsPublic = false },
                new UpdateSettingRequest { Key = UrlKey, Value = url, Category = SettingCategory.Branding, IsPublic = true },
            ], ct);
            return url;
        }

        public static async Task ClearAsync(ISettingsService settings, CancellationToken ct) =>
            await settings.UpsertAsync(
            [
                new UpdateSettingRequest { Key = FileKey, Value = null, Category = SettingCategory.Branding, IsPublic = false },
                new UpdateSettingRequest { Key = UrlKey, Value = null, Category = SettingCategory.Branding, IsPublic = true },
            ], ct);

        public static async Task<(Stream Content, string ContentType)?> OpenAsync(IFileStorage storage, ISettingsService settings, CancellationToken ct)
        {
            var pointer = (await settings.GetAllAsync(ct)).FirstOrDefault(x => x.Key == FileKey)?.Value;
            var split = pointer?.LastIndexOf('|') ?? -1;
            if (pointer is null || split <= 0) return null;
            var stream = await storage.OpenReadAsync(pointer[..split], ct);
            return stream is null ? null : (stream, pointer[(split + 1)..]);
        }

        /// <summary>
        /// Absolute URL of the API for the tenant in scope: Api:BaseUrl when configured, otherwise the request's own host,
        /// with the /t/{code} prefix added. Must be called while the tenant is bound (inside a tenant request or scope).
        /// </summary>
        public static string PublicBaseUrl(IConfiguration configuration, HttpRequest request)
        {
            var root = configuration["Api:BaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(root)) root = $"{request.Scheme}://{request.Host}";
            return TenantExecution.Url(root);
        }
    }
}
