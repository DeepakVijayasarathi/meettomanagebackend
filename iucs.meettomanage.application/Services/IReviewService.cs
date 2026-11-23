using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Dto.Common;

namespace iucs.meettomanage.application.Services
{
    /// <summary>
    /// Handles reviews (public social proof) and review requests (outbound collection).
    /// Reviews are platform-level — visible on the marketing site for all visitors.
    /// ReviewRequests are outbound invitations to customers to leave reviews.
    /// </summary>
    public interface IReviewService
    {
        // ===== Reviews (public social proof) =====

        /// <summary>Create a new review (platform admin).</summary>
        Task<ReviewDto> CreateReviewAsync(CreateReviewRequest request, CancellationToken ct = default);

        /// <summary>Update an existing review (platform admin).</summary>
        Task<ReviewDto> UpdateReviewAsync(Guid id, UpdateReviewRequest request, CancellationToken ct = default);

        /// <summary>Delete a review (soft delete).</summary>
        Task DeleteReviewAsync(Guid id, CancellationToken ct = default);

        /// <summary>Get the aggregate rating summary (for homepage, pricing page).</summary>
        Task<AggregateRatingDto> GetAggregateRatingAsync(CancellationToken ct = default);

        /// <summary>Get visible public reviews, ordered by newest first.</summary>
        Task<PagedResult<PublicReviewDto>> GetPublicReviewsAsync(
            int page = 1,
            int pageSize = 10,
            CancellationToken ct = default);

        /// <summary>Get a single review by ID (platform admin).</summary>
        Task<ReviewDto?> GetReviewAsync(Guid id, CancellationToken ct = default);

        /// <summary>Get all reviews for a specific academy (if linked).</summary>
        Task<IReadOnlyList<ReviewDto>> GetReviewsByAcademyAsync(Guid academyTenantId, CancellationToken ct = default);

        // ===== Review Requests (outbound collection) =====

        /// <summary>Create a pending review request.</summary>
        Task<ReviewRequestDto> CreateReviewRequestAsync(CreateReviewRequestRequest request, CancellationToken ct = default);

        /// <summary>Update the status of a review request.</summary>
        Task<ReviewRequestDto> UpdateReviewRequestStatusAsync(Guid id, UpdateReviewRequestStatusRequest request, CancellationToken ct = default);

        /// <summary>Mark a review request as sent (with the URL that was sent).</summary>
        Task<ReviewRequestDto> MarkReviewRequestSentAsync(Guid id, string sentUrl, CancellationToken ct = default);

        /// <summary>Get pending review requests that haven't expired.</summary>
        Task<IReadOnlyList<ReviewRequestDto>> GetPendingReviewRequestsAsync(CancellationToken ct = default);

        /// <summary>Get review requests for a specific academy.</summary>
        Task<IReadOnlyList<ReviewRequestDto>> GetReviewRequestsByAcademyAsync(Guid academyTenantId, CancellationToken ct = default);
    }
}
