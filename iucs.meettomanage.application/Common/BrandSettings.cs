using iucs.meettomanage.domain.Entities.Settings;
using iucs.meettomanage.domain.Repository;

namespace iucs.meettomanage.application.Common
{
    /// <summary>
    /// Reads the admin's Settings &amp; Branding AppSettings (brand.name, brand.primaryColor) —
    /// the same values the frontend's reactive useBrand() reads for page title/logo/UI text —
    /// for the few places backend-rendered content needs the org's display name or brand
    /// color (the Razorpay checkout popup, an email's fallback subject, the parent-facing
    /// invoice HTML). Mirrors NotificationToggles' own single-key AppSetting read.
    /// </summary>
    public static class BrandSettings
    {
        public const string NameKey = "brand.name";
        public const string DefaultName = "Meet to Manage";

        public const string PrimaryColorKey = "brand.primaryColor";
        public const string DefaultPrimaryColor = "#1E3A5F";

        public static async Task<string> GetNameAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
        {
            var setting = await unitOfWork.Repository<AppSetting>()
                .FirstOrDefaultAsync(s => s.Key == NameKey, cancellationToken);
            return string.IsNullOrWhiteSpace(setting?.Value) ? DefaultName : setting.Value.Trim();
        }

        public static async Task<string> GetPrimaryColorAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
        {
            var setting = await unitOfWork.Repository<AppSetting>()
                .FirstOrDefaultAsync(s => s.Key == PrimaryColorKey, cancellationToken);
            return string.IsNullOrWhiteSpace(setting?.Value) ? DefaultPrimaryColor : setting.Value.Trim();
        }
    }
}
