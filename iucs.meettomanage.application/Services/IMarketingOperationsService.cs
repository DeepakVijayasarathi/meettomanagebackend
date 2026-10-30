using iucs.meettomanage.application.Dto.Marketing;

namespace iucs.meettomanage.application.Services;

public interface IMarketingOperationsService
{
    Task<IReadOnlyList<MarketingCampaignBudgetDto>> ListCampaignsAsync(CancellationToken cancellationToken = default);
    Task<MarketingCampaignBudgetDto> SaveCampaignAsync(MarketingCampaignBudgetDto request, CancellationToken cancellationToken = default);
    Task DeleteCampaignAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MarketingReportSettingDto> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<MarketingReportSettingDto> SaveSettingsAsync(MarketingReportSettingDto request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MarketingTeamMemberDto>> TeamAsync(CancellationToken cancellationToken = default);
    Task ApplyRetentionAsync(CancellationToken cancellationToken = default);
}
