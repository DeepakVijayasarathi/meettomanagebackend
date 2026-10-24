using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>
    /// Everything the mobile parent app needs to know about the organisation it signed in to. The app is built with a
    /// single API address; the rest (web portal address, name, logo, colours, support contact, announcement) is set by
    /// the administrator in Tenant configuration and read here after sign-in, so changing it needs no app release.
    /// </summary>
    [ApiController]
    [Route("api/app/config")]
    [Authorize]
    public class AppConfigController(ISettingsService settings, TenantContext tenant, IConfiguration configuration) : ControllerBase
    {
        /// <summary>Settings a signed-in family may read. Anything not listed here is never sent to the app.</summary>
        private static readonly string[] Keys =
        [
            "org.name", "org.supportEmail", "org.supportPhone",
            "brand.name", "brand.logoUrl", "brand.primaryColor", "brand.accentColor", "brand.fontFamily", "brand.headingFont",
            "app.webBaseUrl", "app.announcement", "app.joinOpensMinutesBefore",
        ];

        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken cancellationToken)
        {
            var values = (await settings.GetAllAsync(cancellationToken))
                .Where(x => Keys.Contains(x.Key))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            string? Value(string key) => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

            // The portal address: the administrator's per-organisation override, else the server's own frontend address.
            var web = Value("app.webBaseUrl") ?? configuration["Frontend:BaseUrl"];
            // How many minutes before a class starts the app enables Join Class; 10 unless the administrator sets another.
            var joinOpens = int.TryParse(Value("app.joinOpensMinutesBefore"), out var minutes) ? Math.Clamp(minutes, 0, 1440) : 10;
            return Ok(new
            {
                tenantCode = tenant.TenantCode,
                appAccessGranted = tenant.AppAccessGranted,
                webBaseUrl = IsHttpUrl(web) ? web!.TrimEnd('/') : null,
                organisationName = Value("org.name"),
                supportEmail = Value("org.supportEmail"),
                supportPhone = Value("org.supportPhone"),
                announcement = Value("app.announcement"),
                joinOpensMinutesBefore = joinOpens,
                brand = new
                {
                    name = Value("brand.name"),
                    logoUrl = Value("brand.logoUrl"),
                    primaryColor = Value("brand.primaryColor"),
                    accentColor = Value("brand.accentColor"),
                    fontFamily = Value("brand.fontFamily"),
                    headingFont = Value("brand.headingFont"),
                },
            });
        }

        /// <summary>Only an absolute http(s) address is handed to the app; anything else (e.g. javascript:) is dropped.</summary>
        public static bool IsHttpUrl(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
