using System.Runtime.CompilerServices;
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

        // Fan-out sends (e.g. one notification email per recipient in a batch) call this once
        // per recipient using the same request-scoped IUnitOfWork — a fresh SELECT per call
        // was a real N+1. Cached per-IUnitOfWork (via a weak table so it never outlives the
        // scoped instance it's keyed on) rather than process-wide: IUnitOfWork is Scoped/
        // per-request, so this dies with the request automatically — no TTL or cross-request
        // invalidation to get wrong, and no risk of one request seeing another's brand-name
        // update or of the value going stale across an admin edit.
        private static readonly ConditionalWeakTable<IUnitOfWork, StrongBox<string>> Cache = new();

        public static async Task<string> GetNameAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
        {
            if (Cache.TryGetValue(unitOfWork, out var box))
            {
                return box.Value!;
            }

            var setting = await unitOfWork.Repository<AppSetting>()
                .FirstOrDefaultAsync(s => s.Key == NameKey, cancellationToken);
            var name = string.IsNullOrWhiteSpace(setting?.Value) ? DefaultName : setting.Value.Trim();
            Cache.AddOrUpdate(unitOfWork, new StrongBox<string>(name));
            return name;
        }

        public static async Task<string> GetPrimaryColorAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
        {
            var setting = await unitOfWork.Repository<AppSetting>()
                .FirstOrDefaultAsync(s => s.Key == PrimaryColorKey, cancellationToken);
            return string.IsNullOrWhiteSpace(setting?.Value) ? DefaultPrimaryColor : setting.Value.Trim();
        }
    }
}
