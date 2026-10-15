using iucs.meettomanage.application.Dto.Settings;

namespace iucs.meettomanage.application.Services
{
    public interface ISettingsService
    {
        /// <summary>Every setting row, for the admin Settings &amp; Branding screen.</summary>
        Task<IReadOnlyList<SettingDto>> GetAllAsync(CancellationToken cancellationToken = default);

        /// <summary>Public rows only (branding); safe to serve before login.</summary>
        Task<IReadOnlyList<SettingDto>> GetPublicAsync(CancellationToken cancellationToken = default);

        /// <summary>Bulk upsert with replace-by-key semantics; the screen submits only changed keys.</summary>
        Task<IReadOnlyList<SettingDto>> UpsertAsync(
            IReadOnlyList<UpdateSettingRequest> updates,
            CancellationToken cancellationToken = default);

        /// <summary>The marketing site's own settings (seo.*, analytics.*), for the marketing console only.</summary>
        Task<IReadOnlyList<SettingDto>> GetMarketingAsync(CancellationToken cancellationToken = default);

        /// <summary>Upsert limited to marketing keys; always stored public (the marketing site reads them pre-login).</summary>
        Task<IReadOnlyList<SettingDto>> UpsertMarketingAsync(
            IReadOnlyList<UpdateSettingRequest> updates,
            CancellationToken cancellationToken = default);
    }
}
