using iucs.readernest.application.Dto.Admission;
using iucs.readernest.domain.Enums;

namespace iucs.readernest.application.Services
{
    /// <summary>Backs the public course catalog (/store) and the admin follow-up queue for it.</summary>
    public interface IStoreService
    {
        /// <summary>Active plans only — this is the public-facing catalog.</summary>
        Task<IReadOnlyList<StorePlanDto>> ListPublicPlansAsync(CancellationToken cancellationToken = default);

        Task<StoreInquiryDto> CreateInquiryAsync(CreateStoreInquiryRequest request, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<StoreInquiryDto>> ListInquiriesAsync(StoreInquiryStatus? status, CancellationToken cancellationToken = default);

        Task<StoreInquiryDto> UpdateInquiryStatusAsync(
            Guid id,
            UpdateStoreInquiryStatusRequest request,
            CancellationToken cancellationToken = default);
    }
}
