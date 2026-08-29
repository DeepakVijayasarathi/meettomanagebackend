using iucs.meettomanage.application.Dto.Marketing;

namespace iucs.meettomanage.application.Services
{
    /// <summary>Backs the public marketing blog (/blog) and its admin editor.</summary>
    public interface IBlogService
    {
        /// <summary>Published posts only, newest first — the public /blog listing.</summary>
        Task<IReadOnlyList<BlogPostSummaryDto>> ListPublishedAsync(CancellationToken cancellationToken = default);

        /// <summary>A single published post by slug — the public /blog/{slug} page.</summary>
        Task<BlogPostDetailDto> GetPublishedBySlugAsync(string slug, CancellationToken cancellationToken = default);

        /// <summary>Every post, published or not — the admin list.</summary>
        Task<IReadOnlyList<BlogPostDto>> ListAllAsync(CancellationToken cancellationToken = default);

        Task<BlogPostDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

        Task<BlogPostDto> CreateAsync(CreateBlogPostRequest request, CancellationToken cancellationToken = default);

        Task<BlogPostDto> UpdateAsync(Guid id, UpdateBlogPostRequest request, CancellationToken cancellationToken = default);

        Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    }
}
