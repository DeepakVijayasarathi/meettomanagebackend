using iucs.meettomanage.api.Auth;
using iucs.meettomanage.api.Services;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>The client's uploaded logo: public to read (the login screen needs it), permission-gated to change.</summary>
    [ApiController]
    [Route("api/settings/brand-logo")]
    public class BrandLogoController(IFileStorage storage, ISettingsService settings, IConfiguration configuration) : ControllerBase
    {
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Get(CancellationToken cancellationToken)
        {
            var logo = await BrandLogo.OpenAsync(storage, settings, cancellationToken);
            if (logo is null) return NotFound();
            // The ?v= in the URL changes on every upload, so a long cache is safe. The sandbox CSP keeps the response inert
            // even if someone opens the URL directly instead of using it in an <img>.
            Response.Headers.CacheControl = "public, max-age=86400";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            return File(logo.Value.Content, logo.Value.ContentType);
        }

        [HttpPost]
        [HasPermission(PermissionModule.Settings, PermissionAction.Edit)]
        [RequestSizeLimit(BrandLogo.MaxBytes + 4096)]
        public async Task<IActionResult> Upload(IFormFile file, CancellationToken cancellationToken)
        {
            await using var stream = file.OpenReadStream();
            var url = await BrandLogo.SaveAsync(storage, settings, stream, file.FileName,
                BrandLogo.PublicBaseUrl(configuration, Request), cancellationToken);
            return Ok(new { logoUrl = url });
        }

        [HttpDelete]
        [HasPermission(PermissionModule.Settings, PermissionAction.Edit)]
        public async Task<IActionResult> Remove(CancellationToken cancellationToken)
        {
            await BrandLogo.ClearAsync(settings, cancellationToken);
            return NoContent();
        }
    }
}
