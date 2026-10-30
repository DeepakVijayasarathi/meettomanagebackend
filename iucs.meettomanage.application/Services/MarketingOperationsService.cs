using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.application.Services;

public class MarketingOperationsService(IUnitOfWork unitOfWork) : IMarketingOperationsService
{
    public async Task<IReadOnlyList<MarketingCampaignBudgetDto>> ListCampaignsAsync(CancellationToken ct = default)
    {
        var budgets = await unitOfWork.Repository<MarketingCampaignBudget>().Query().OrderByDescending(x => x.StartDate).ToListAsync(ct);
        var result = new List<MarketingCampaignBudgetDto>();
        foreach (var budget in budgets)
        {
            var start = DateTime.SpecifyKind(budget.StartDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var end = DateTime.SpecifyKind(budget.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var leads = await unitOfWork.Repository<PlatformDemoRequest>().Query().Where(x => x.Campaign == budget.Campaign && x.CreatedAtUtc >= start && x.CreatedAtUtc < end).ToListAsync(ct);
            var converted = leads.Count(x => x.Status == StoreInquiryStatus.Converted);
            result.Add(ToDto(budget, leads.Count, converted));
        }
        return result;
    }

    public async Task<MarketingCampaignBudgetDto> SaveCampaignAsync(MarketingCampaignBudgetDto request, CancellationToken ct = default)
    {
        if (request.EndDate < request.StartDate) throw new ArgumentException("Campaign end date must be on or after its start date.");
        MarketingCampaignBudget row;
        if (request.Id == Guid.Empty) { row = new() { Id = Guid.NewGuid() }; await unitOfWork.Repository<MarketingCampaignBudget>().AddAsync(row, ct); }
        else row = await unitOfWork.Repository<MarketingCampaignBudget>().FirstOrDefaultAsync(x => x.Id == request.Id, ct) ?? throw new NotFoundException(nameof(MarketingCampaignBudget), request.Id);
        row.Campaign = request.Campaign.Trim().ToLowerInvariant(); row.Spend = request.Spend; row.Currency = request.Currency.ToUpperInvariant(); row.WonRevenue = request.WonRevenue; row.StartDate = request.StartDate; row.EndDate = request.EndDate;
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(row, 0, 0);
    }

    public async Task DeleteCampaignAsync(Guid id, CancellationToken ct = default)
    {
        var row = await unitOfWork.Repository<MarketingCampaignBudget>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(MarketingCampaignBudget), id);
        unitOfWork.Repository<MarketingCampaignBudget>().Remove(row); await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<MarketingReportSettingDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var row = await unitOfWork.Repository<MarketingReportSetting>().Query().OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        return row is null ? new() { Enabled = false, Frequency = "Weekly", RecipientEmail = "", RetentionDays = 400 } : ToDto(row);
    }

    public async Task<MarketingReportSettingDto> SaveSettingsAsync(MarketingReportSettingDto request, CancellationToken ct = default)
    {
        var row = await unitOfWork.Repository<MarketingReportSetting>().FirstOrDefaultAsync(_ => true, ct);
        if (row is null) { row = new() { Id = Guid.NewGuid() }; await unitOfWork.Repository<MarketingReportSetting>().AddAsync(row, ct); }
        row.Enabled = request.Enabled; row.Frequency = request.Frequency; row.RecipientEmail = request.RecipientEmail.Trim().ToLowerInvariant(); row.RetentionDays = request.RetentionDays;
        await unitOfWork.SaveChangesAsync(ct); return ToDto(row);
    }

    public async Task<IReadOnlyList<MarketingTeamMemberDto>> TeamAsync(CancellationToken ct = default) => await unitOfWork.Repository<User>().Query()
        .Where(x => x.Role == UserRole.Marketing && x.Status == UserStatus.Active).OrderBy(x => x.FirstName)
        .Select(x => new MarketingTeamMemberDto { Id = x.Id, Name = (x.FirstName + " " + x.LastName).Trim(), Email = x.Email }).ToListAsync(ct);

    public async Task ApplyRetentionAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct); var cutoff = DateTime.UtcNow.AddDays(-settings.RetentionDays);
        await unitOfWork.Repository<MarketingEvent>().ExecuteHardDeleteAsync(x => x.CreatedAtUtc < cutoff, ct);
        await unitOfWork.Repository<MarketingLeadActivity>().ExecuteHardDeleteAsync(x => x.CreatedAtUtc < cutoff, ct);
    }

    private static MarketingCampaignBudgetDto ToDto(MarketingCampaignBudget x, int leads, int converted) => new()
    {
        Id = x.Id, Campaign = x.Campaign, Spend = x.Spend, Currency = x.Currency, WonRevenue = x.WonRevenue, StartDate = x.StartDate, EndDate = x.EndDate, Leads = leads, Converted = converted,
        CostPerLead = leads == 0 ? null : Math.Round(x.Spend / leads, 2), CostPerAcquisition = converted == 0 ? null : Math.Round(x.Spend / converted, 2),
        ReturnOnAdSpend = x.Spend == 0 || !x.WonRevenue.HasValue ? null : Math.Round(x.WonRevenue.Value / x.Spend, 2),
    };
    private static MarketingReportSettingDto ToDto(MarketingReportSetting x) => new() { Enabled = x.Enabled, Frequency = x.Frequency, RecipientEmail = x.RecipientEmail, RetentionDays = x.RetentionDays, LastSentAtUtc = x.LastSentAtUtc };
}
