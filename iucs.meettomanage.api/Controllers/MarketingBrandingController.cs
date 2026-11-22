using System.Security.Cryptography;
using System.Text;
using iucs.meettomanage.api.Services;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.domain.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Controllers;

[ApiController, Route("api/platform/marketing-branding"), Authorize(Policy = "PlatformAdmin")]
public sealed class MarketingBrandingController(IDbContextFactory<MasterDbContext> factory,
    [FromKeyedServices("platform-marketing")] IFileStorage storage) : ControllerBase
{
    private const string SettingKey = "marketing.logoFile";
    private const string PublicPath = "/api/platform/public/marketing-logo";

    [HttpGet]
    [ResponseCache(NoStore = true)]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var pointer = await ReadPointerAsync(ct);
        return Ok(new { logoPath = pointer is null ? null : LogoPath(pointer) });
    }

    [HttpGet(PublicPath)]
    [AllowAnonymous]
    [ResponseCache(NoStore = true)]
    public async Task<IActionResult> GetLogo(CancellationToken ct)
    {
        var pointer = await ReadPointerAsync(ct);
        if (pointer is null) return NotFound();
        var parts = pointer.Split('|', 2);
        var stream = await storage.OpenReadAsync(parts[0], ct);
        if (stream is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return File(stream, parts[1]);
    }

    [HttpPost("logo")]
    [RequestSizeLimit(BrandLogo.MaxBytes + 4096)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) throw new DomainValidationException("The uploaded file is empty.");
        if (file.Length > BrandLogo.MaxBytes) throw new DomainValidationException("The logo must be 1 MB or smaller.");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var contentType = BrandLogo.Detect(buffer.ToArray(), file.FileName)
            ?? throw new DomainValidationException("Upload a PNG, JPG or WebP image.");
        var extension = contentType switch { "image/png" => ".png", "image/jpeg" => ".jpg", _ => ".webp" };
        buffer.Position = 0;
        var stored = await storage.StoreAsync(buffer, "marketing-logo" + extension, ct);
        var pointer = $"{stored.RelativePath}|{contentType}";

        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform_setting (id, key, value, updated_at_utc)
            VALUES ({Guid.NewGuid()}, {SettingKey}, {pointer}, {DateTime.UtcNow})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = EXCLUDED.updated_at_utc
            """, ct);
        return Ok(new { logoPath = LogoPath(pointer) });
    }

    [HttpDelete("logo")]
    public async Task<IActionResult> Remove(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.PlatformSettings.Where(setting => setting.Key == SettingKey).ExecuteDeleteAsync(ct);
        return NoContent();
    }

    private async Task<string?> ReadPointerAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.PlatformSettings.Where(setting => setting.Key == SettingKey)
            .Select(setting => setting.Value).SingleOrDefaultAsync(ct);
    }

    private static string LogoPath(string pointer) =>
        $"{PublicPath}?v={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pointer)))[..16]}";
}
