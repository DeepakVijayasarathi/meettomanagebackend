using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Services
{
    /// <summary>Backs the public "request a platform demo" form and its admin follow-up queue.</summary>
    public interface IPlatformDemoRequestService
    {
        Task<PlatformDemoRequestDto> CreateAsync(CreatePlatformDemoRequestRequest request, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PlatformDemoRequestDto>> ListAsync(StoreInquiryStatus? status, CancellationToken cancellationToken = default);

        Task<PlatformDemoRequestDto> UpdateStatusAsync(
            Guid id,
            UpdatePlatformDemoRequestStatusRequest request,
            CancellationToken cancellationToken = default);
    }
}
