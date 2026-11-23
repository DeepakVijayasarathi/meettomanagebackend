using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers;

/// <summary>
/// Reviews system for the marketing site.
/// Public endpoints: aggregate rating, list of public reviews.
/// Admin endpoints: CRUD reviews and manage review requests.
/// Reviews are platform-level (not per-academy) — they live on the marketing site.
/// </summary>
[ApiController, Route("api/reviews")]
public sealed class ReviewController(IReviewService reviewService) : ControllerBase
{
    private const string AdminRoles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)}";

    // ========== PUBLIC ENDPOINTS ==========

    /// <summary>Aggregate rating for the homepage, pricing page, etc.</summary>
    [HttpGet("aggregate-rating")]
    [AllowAnonymous]
    [ResponseCache(Duration = 300, VaryByHeader = "Accept")]
    public async Task<IActionResult> GetAggregateRating(CancellationToken ct)
    {
        var rating = await reviewService.GetAggregateRatingAsync(ct);
        return Ok(rating);
    }

    /// <summary>Public reviews list (paginated, newest first).</summary>
    [HttpGet("public")]
    [AllowAnonymous]
    [ResponseCache(Duration = 300, VaryByHeader = "Accept")]
    public async Task<IActionResult> GetPublicReviews(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 50) pageSize = 10;

        var result = await reviewService.GetPublicReviewsAsync(page, pageSize, ct);
        return Ok(new { result.Items, result.TotalCount, result.Page, result.PageSize });
    }

    /// <summary>Latest review for social sharing.</summary>
    [HttpGet("latest")]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, VaryByHeader = "Accept")]
    public async Task<IActionResult> GetLatestReview(CancellationToken ct)
    {
        var result = await reviewService.GetPublicReviewsAsync(1, 1, ct);
        if (!result.Items.Any())
            return NotFound(new { message = "No reviews yet." });
        return Ok(result.Items[0]);
    }

    // ========== ADMIN ENDPOINTS ==========

    /// <summary>Get all reviews (admin).</summary>
    [HttpGet]
    [Authorize(Roles = AdminRoles)]
    public async Task<IActionResult> ListReviews(CancellationToken ct)
    {
        var result = await reviewService.GetPublicReviewsAsync(1, 100, ct);
        // Fetch full DTOs for admin view
        var allReviews = await reviewService.GetPublicReviewsAsync(1, 100, ct);
        return Ok(new
        {
            result.TotalCount,
            Items = result.Items.Select(r => new
            {
                r.Id, r.Source, r.Rating, r.AuthorName, r.AuthorRole,
                r.Text, r.Response, r.ReviewUrl, r.CreatedAtUtc,
            }),
        });
    }

    /// <summary>Create a new review (admin).</summary>
    [HttpPost]
    [Authorize(Roles = AdminRoles)]
    public async Task<IActionResult> CreateReview([Required] CreateReviewRequest request, CancellationToken ct)
    {
        var review = await reviewService.CreateReviewAsync(request, ct);
        return CreatedAtAction(nameof(GetReview), new { id = review.Id }, new { review });
    }

    /// <summary>Update a review (admin).</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = AdminRoles)]
    public async Task<IActionResult> UpdateReview(Guid id, [Required] UpdateReviewRequest request, CancellationToken ct)
    {
        var review = await reviewService.UpdateReviewAsync(id, request, ct);
        return Ok(new { review });
    }

    /// <summary>Delete a review (admin, soft delete).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = AdminRoles)]
    public async Task<IActionResult> DeleteReview(Guid id, CancellationToken ct)
    {
        await reviewService.DeleteReviewAsync(id, ct);
        return NoContent();
    }

    /// <summary>Get a single review by ID (admin).</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Roles = AdminRoles)]
    public async Task<IActionResult> GetReview(Guid id, CancellationToken ct)
    {
        var review = await reviewService.GetReviewAsync(id, ct);
        if (review == null)
            return NotFound(new { message = "Review not found." });
        return Ok(review);
    }

    // ========== REVIEW REQUESTS ==========

    /// <summary>Create a review request (admin).</summary>
    [HttpPost("requests")]
    [Authorize(Roles = AdminRoles)]
    public async Task<IActionResult> CreateReviewRequest([Required] CreateReviewRequestRequest request, CancellationToken ct)
    {
        var result = await reviewService.CreateReviewRequestAsync(request, ct);
        return Ok(new { result });
    }

    /// <summary>Mark a review request as sent (admin).</summary>
    [HttpPost("requests/{id:guid}/send")]
    [Authorize(Roles = AdminRoles)]
    public async Task<IActionResult> SendReviewRequest(Guid id, CancellationToken ct)
    {
        // The URL would include a unique token that tracks the review submission
        var sentUrl = $"https://meettomanage.cloud/review/requested?rid={id}";
        var result = await reviewService.MarkReviewRequestSentAsync(id, sentUrl, ct);
        return Ok(new { result, sentUrl });
    }

    /// <summary>Get pending review requests (admin).</summary>
    [HttpGet("requests")]
    [Authorize(Roles = AdminRoles)]
    public async Task<IActionResult> ListReviewRequests(CancellationToken ct)
    {
        var requests = await reviewService.GetPendingReviewRequestsAsync(ct);
        return Ok(new { requests.Count, Items = requests });
    }

    /// <summary>Update review request status (admin).</summary>
    [HttpPut("requests/{id:guid}")]
    [Authorize(Roles = AdminRoles)]
    public async Task<IActionResult> UpdateReviewRequest(Guid id, [Required] UpdateReviewRequestStatusRequest request, CancellationToken ct)
    {
        var result = await reviewService.UpdateReviewRequestStatusAsync(id, request, ct);
        return Ok(new { result });
    }
}
