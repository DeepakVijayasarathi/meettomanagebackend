using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Dto.Common;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.application.Services
{
    public class ReviewService : IReviewService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;

        public ReviewService(IUnitOfWork unitOfWork, IAuditLogService auditLog)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
        }

        // ===== Reviews =====

        public async Task<ReviewDto> CreateReviewAsync(CreateReviewRequest request, CancellationToken ct = default)
        {
            var review = new Review
            {
                Id = Guid.NewGuid(),
                Source = request.Source.ToLowerInvariant(),
                Rating = request.Rating,
                AuthorName = request.AuthorName.Trim(),
                AuthorRole = request.AuthorRole?.Trim(),
                Text = request.Text.Trim(),
                Response = request.Response?.Trim(),
                ReviewUrl = request.ReviewUrl?.Trim(),
                IsVisible = request.IsVisible,
                AcademyTenantId = request.AcademyTenantId,
            };

            await _unitOfWork.Repository<Review>().AddAsync(review, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            await _auditLog.StageAsync(
                AuditAction.Create,
                "Review",
                review.Id.ToString(),
                null,
                ct);

            return new ReviewDto
            {
                Id = review.Id,
                Source = review.Source,
                Rating = review.Rating,
                AuthorName = review.AuthorName,
                AuthorRole = review.AuthorRole,
                Text = review.Text,
                Response = review.Response,
                ReviewUrl = review.ReviewUrl,
                IsVisible = review.IsVisible,
                AcademyTenantId = review.AcademyTenantId,
                CreatedAtUtc = review.CreatedAtUtc,
            };
        }

        public async Task<ReviewDto> UpdateReviewAsync(Guid id, UpdateReviewRequest request, CancellationToken ct = default)
        {
            var review = await _unitOfWork.Repository<Review>().Query()
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);

            if (review == null)
                throw new NotFoundException("Review not found.");

            if (request.Rating.HasValue) review.Rating = request.Rating.Value;
            if (request.AuthorName != null) review.AuthorName = request.AuthorName.Trim();
            if (request.AuthorRole != null) review.AuthorRole = request.AuthorRole.Trim();
            if (request.Text != null) review.Text = request.Text.Trim();
            if (request.Response != null) review.Response = request.Response.Trim();
            if (request.ReviewUrl != null) review.ReviewUrl = request.ReviewUrl.Trim();
            if (request.IsVisible.HasValue) review.IsVisible = request.IsVisible.Value;

            await _unitOfWork.SaveChangesAsync(ct);
            await _auditLog.StageAsync(
                AuditAction.Update,
                "Review",
                review.Id.ToString(),
                null,
                ct);

            return new ReviewDto
            {
                Id = review.Id,
                Source = review.Source,
                Rating = review.Rating,
                AuthorName = review.AuthorName,
                AuthorRole = review.AuthorRole,
                Text = review.Text,
                Response = review.Response,
                ReviewUrl = review.ReviewUrl,
                IsVisible = review.IsVisible,
                AcademyTenantId = review.AcademyTenantId,
                CreatedAtUtc = review.CreatedAtUtc,
            };
        }

        public async Task DeleteReviewAsync(Guid id, CancellationToken ct = default)
        {
            var review = await _unitOfWork.Repository<Review>().Query()
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);

            if (review == null)
                throw new NotFoundException("Review not found.");

            review.IsDeleted = true;
            review.DeletedAtUtc = DateTime.UtcNow;

            await _unitOfWork.SaveChangesAsync(ct);
            await _auditLog.StageAsync(
                AuditAction.Delete,
                "Review",
                review.Id.ToString(),
                null,
                ct);
        }

        public async Task<AggregateRatingDto> GetAggregateRatingAsync(CancellationToken ct = default)
        {
            var query = _unitOfWork.Repository<Review>().Query().Where(x => !x.IsDeleted && x.IsVisible);
            var visibleReviews = await query.ToListAsync(ct);

            if (!visibleReviews.Any())
                return new AggregateRatingDto { AverageRating = 0, TotalReviews = 0, VisibleReviews = 0, RatingBreakdown = new(), ReviewsBySource = new() };

            var allReviews = await _unitOfWork.Repository<Review>().Query().Where(x => !x.IsDeleted).ToListAsync(ct);

            var avgRating = visibleReviews.Average(r => (double)r.Rating);
            var breakdown = visibleReviews.GroupBy(r => r.Rating)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());
            var bySource = visibleReviews.GroupBy(r => r.Source)
                .ToDictionary(g => g.Key, g => g.Count());

            return new AggregateRatingDto
            {
                AverageRating = Math.Round(avgRating, 1),
                TotalReviews = allReviews.Count,
                VisibleReviews = visibleReviews.Count,
                RatingBreakdown = breakdown,
                ReviewsBySource = bySource,
            };
        }

        public async Task<PagedResult<PublicReviewDto>> GetPublicReviewsAsync(
            int page = 1,
            int pageSize = 10,
            CancellationToken ct = default)
        {
            var query = _unitOfWork.Repository<Review>().Query()
                .Where(x => !x.IsDeleted && x.IsVisible)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id);

            var totalCount = await query.CountAsync(ct);
            var reviews = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new PublicReviewDto
                {
                    Id = x.Id,
                    Source = x.Source,
                    Rating = x.Rating,
                    AuthorName = x.AuthorName,
                    AuthorRole = x.AuthorRole,
                    Text = x.Text,
                    Response = x.Response,
                    ReviewUrl = x.ReviewUrl,
                    CreatedAtUtc = x.CreatedAtUtc,
                })
                .ToListAsync(ct);

            return new PagedResult<PublicReviewDto>
            {
                Items = reviews,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
            };
        }

        public async Task<ReviewDto?> GetReviewAsync(Guid id, CancellationToken ct = default)
        {
            var review = await _unitOfWork.Repository<Review>().Query()
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);

            if (review == null) return null;

            return new ReviewDto
            {
                Id = review.Id,
                Source = review.Source,
                Rating = review.Rating,
                AuthorName = review.AuthorName,
                AuthorRole = review.AuthorRole,
                Text = review.Text,
                Response = review.Response,
                ReviewUrl = review.ReviewUrl,
                IsVisible = review.IsVisible,
                AcademyTenantId = review.AcademyTenantId,
                CreatedAtUtc = review.CreatedAtUtc,
            };
        }

        public async Task<IReadOnlyList<ReviewDto>> GetReviewsByAcademyAsync(Guid academyTenantId, CancellationToken ct = default)
        {
            var reviews = await _unitOfWork.Repository<Review>().Query()
                .Where(x => !x.IsDeleted && x.AcademyTenantId == academyTenantId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .Select(x => new ReviewDto
                {
                    Id = x.Id,
                    Source = x.Source,
                    Rating = x.Rating,
                    AuthorName = x.AuthorName,
                    AuthorRole = x.AuthorRole,
                    Text = x.Text,
                    Response = x.Response,
                    ReviewUrl = x.ReviewUrl,
                    IsVisible = x.IsVisible,
                    AcademyTenantId = x.AcademyTenantId,
                    CreatedAtUtc = x.CreatedAtUtc,
                })
                .ToListAsync(ct);
            return reviews;
        }

        // ===== Review Requests =====

        public async Task<ReviewRequestDto> CreateReviewRequestAsync(CreateReviewRequestRequest request, CancellationToken ct = default)
        {
            var requestEntity = new ReviewRequest
            {
                Id = Guid.NewGuid(),
                ContactName = request.ContactName.Trim(),
                ContactMethod = request.ContactMethod.ToLowerInvariant(),
                ContactValue = request.ContactValue.Trim(),
                Platform = request.Platform.ToLowerInvariant(),
                Status = "pending",
                AcademyTenantId = request.AcademyTenantId,
            };

            await _unitOfWork.Repository<ReviewRequest>().AddAsync(requestEntity, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return new ReviewRequestDto
            {
                Id = requestEntity.Id,
                ContactName = requestEntity.ContactName,
                ContactMethod = requestEntity.ContactMethod,
                ContactValue = requestEntity.ContactValue,
                Platform = requestEntity.Platform,
                Status = requestEntity.Status,
                SentUrl = requestEntity.SentUrl,
                SentAtUtc = requestEntity.SentAtUtc,
                ExpiresAtUtc = requestEntity.ExpiresAtUtc,
                AcademyTenantId = requestEntity.AcademyTenantId,
                CreatedAtUtc = requestEntity.CreatedAtUtc,
            };
        }

        public async Task<ReviewRequestDto> UpdateReviewRequestStatusAsync(Guid id, UpdateReviewRequestStatusRequest request, CancellationToken ct = default)
        {
            var req = await _unitOfWork.Repository<ReviewRequest>().Query()
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);

            if (req == null)
                throw new NotFoundException("Review request not found.");

            req.Status = request.Status.ToLowerInvariant();
            await _unitOfWork.SaveChangesAsync(ct);

            return new ReviewRequestDto
            {
                Id = req.Id,
                ContactName = req.ContactName,
                ContactMethod = req.ContactMethod,
                ContactValue = req.ContactValue,
                Platform = req.Platform,
                Status = req.Status,
                SentUrl = req.SentUrl,
                SentAtUtc = req.SentAtUtc,
                ExpiresAtUtc = req.ExpiresAtUtc,
                AcademyTenantId = req.AcademyTenantId,
                CreatedAtUtc = req.CreatedAtUtc,
            };
        }

        public async Task<ReviewRequestDto> MarkReviewRequestSentAsync(Guid id, string sentUrl, CancellationToken ct = default)
        {
            var req = await _unitOfWork.Repository<ReviewRequest>().Query()
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);

            if (req == null)
                throw new NotFoundException("Review request not found.");

            req.Status = "sent";
            req.SentUrl = sentUrl.Trim();
            req.SentAtUtc = DateTime.UtcNow;
            req.ExpiresAtUtc = DateTime.UtcNow.AddDays(14);

            await _unitOfWork.SaveChangesAsync(ct);

            return new ReviewRequestDto
            {
                Id = req.Id,
                ContactName = req.ContactName,
                ContactMethod = req.ContactMethod,
                ContactValue = req.ContactValue,
                Platform = req.Platform,
                Status = req.Status,
                SentUrl = req.SentUrl,
                SentAtUtc = req.SentAtUtc,
                ExpiresAtUtc = req.ExpiresAtUtc,
                AcademyTenantId = req.AcademyTenantId,
                CreatedAtUtc = req.CreatedAtUtc,
            };
        }

        public async Task<IReadOnlyList<ReviewRequestDto>> GetPendingReviewRequestsAsync(CancellationToken ct = default)
        {
            var pending = await _unitOfWork.Repository<ReviewRequest>().Query()
                .Where(x => !x.IsDeleted && x.Status == "pending" && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > DateTime.UtcNow))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .Select(x => new ReviewRequestDto
                {
                    Id = x.Id,
                    ContactName = x.ContactName,
                    ContactMethod = x.ContactMethod,
                    ContactValue = x.ContactValue,
                    Platform = x.Platform,
                    Status = x.Status,
                    SentUrl = x.SentUrl,
                    SentAtUtc = x.SentAtUtc,
                    ExpiresAtUtc = x.ExpiresAtUtc,
                    AcademyTenantId = x.AcademyTenantId,
                    CreatedAtUtc = x.CreatedAtUtc,
                })
                .ToListAsync(ct);
            return pending;
        }

        public async Task<IReadOnlyList<ReviewRequestDto>> GetReviewRequestsByAcademyAsync(Guid academyTenantId, CancellationToken ct = default)
        {
            var requests = await _unitOfWork.Repository<ReviewRequest>().Query()
                .Where(x => !x.IsDeleted && x.AcademyTenantId == academyTenantId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .Select(x => new ReviewRequestDto
                {
                    Id = x.Id,
                    ContactName = x.ContactName,
                    ContactMethod = x.ContactMethod,
                    ContactValue = x.ContactValue,
                    Platform = x.Platform,
                    Status = x.Status,
                    SentUrl = x.SentUrl,
                    SentAtUtc = x.SentAtUtc,
                    ExpiresAtUtc = x.ExpiresAtUtc,
                    AcademyTenantId = x.AcademyTenantId,
                    CreatedAtUtc = x.CreatedAtUtc,
                })
                .ToListAsync(ct);
            return requests;
        }
    }
}
