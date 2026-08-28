using iucs.meettomanage.application.Dto.Communication;

namespace iucs.meettomanage.application.Services
{
    public interface IProgressReportService
    {
        Task<IReadOnlyList<ProgressReportDto>> ListAsync(
            int? year,
            int? month,
            Guid? childId,
            CancellationToken cancellationToken = default);

        /// <summary>Visibility rule: a parent sees only their own children's sent reports.</summary>
        Task<IReadOnlyList<ProgressReportDto>> ListForParentUserAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>Draft content only — throws if the report has already been sent.</summary>
        Task<ProgressReportDto> SaveContentAsync(
            Guid id,
            SaveProgressReportContentRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>Emails the current content to the child's parent and locks the report.</summary>
        Task<ProgressReportDto> SendAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates an empty draft for the given period for every active child that doesn't
        /// already have one — called by the monthly background job on the 1st, so staff open
        /// this month's list already populated with one row per child, ready to write.
        /// Returns how many drafts were created.
        /// </summary>
        Task<int> EnsureMonthlyDraftsAsync(int year, int month, CancellationToken cancellationToken = default);
    }
}
