using System.Xml.Linq;
using iucs.meettomanage.api.Controllers;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Services;
using Xunit;

namespace iucs.meettomanage.tests
{
    /// <summary>The blog sitemap lists every published post so search engines find new ones without a redeploy.</summary>
    public class BlogSitemapTests
    {
        private sealed class FakeBlogService(IReadOnlyList<BlogPostSummaryDto> published) : IBlogService
        {
            public Task<IReadOnlyList<BlogPostSummaryDto>> ListPublishedAsync(CancellationToken cancellationToken = default) => Task.FromResult(published);
            public Task<BlogPostDetailDto> GetPublishedBySlugAsync(string slug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<BlogPostDto>> ListAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<BlogPostDto> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<BlogPostDto> CreateAsync(CreateBlogPostRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<BlogPostDto> UpdateAsync(Guid id, UpdateBlogPostRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        [Fact]
        public async Task Sitemap_ListsEveryPublishedPost_WithItsPublishDate()
        {
            var controller = new BlogController(new FakeBlogService(
            [
                new BlogPostSummaryDto { Id = Guid.NewGuid(), Title = "A", Slug = "academy-kpis-to-check-weekly", Excerpt = "", PublishedAtUtc = new DateTime(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc) },
                new BlogPostSummaryDto { Id = Guid.NewGuid(), Title = "B", Slug = "fees-&-billing", Excerpt = "", PublishedAtUtc = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc) },
            ]));

            var result = await controller.Sitemap(CancellationToken.None);

            Assert.StartsWith("application/xml", result.ContentType);
            var ns = XNamespace.Get("http://www.sitemaps.org/schemas/sitemap/0.9");
            var urls = XDocument.Parse(result.Content!).Root!.Elements(ns + "url").ToList();
            Assert.Equal(2, urls.Count);
            Assert.Equal("https://meettomanage.cloud/blog/academy-kpis-to-check-weekly", urls[0].Element(ns + "loc")!.Value);
            Assert.Equal("2026-10-30", urls[0].Element(ns + "lastmod")!.Value);
            // Odd characters in a slug are URL-encoded and the XML stays valid.
            Assert.Equal("https://meettomanage.cloud/blog/fees-%26-billing", urls[1].Element(ns + "loc")!.Value);
        }
    }
}
