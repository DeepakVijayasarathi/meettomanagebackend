using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>Public marketing blog — no login required.</summary>
    [ApiController]
    [Route("api/blog")]
    public class BlogController : ControllerBase
    {
        private readonly IBlogService _blogService;

        public BlogController(IBlogService blogService)
        {
            _blogService = blogService;
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyList<BlogPostSummaryDto>>> List(CancellationToken cancellationToken)
        {
            return Ok(await _blogService.ListPublishedAsync(cancellationToken));
        }

        /// <summary>
        /// Search-engine sitemap of every published post, so a scheduled post is listed the
        /// moment it goes live without a website redeploy. The website serves it as
        /// https://meettomanage.cloud/sitemap-blog.xml (nginx proxy), referenced from its
        /// /sitemap.xml index.
        /// </summary>
        [HttpGet("sitemap.xml")]
        [ResponseCache(Duration = 3600)]
        public async Task<ContentResult> Sitemap(CancellationToken cancellationToken)
        {
            const string site = "https://meettomanage.cloud";
            var posts = await _blogService.ListPublishedAsync(cancellationToken);
            var xml = new System.Text.StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            xml.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
            foreach (var post in posts)
            {
                var loc = System.Security.SecurityElement.Escape($"{site}/blog/{Uri.EscapeDataString(post.Slug)}");
                xml.Append("  <url>\n");
                xml.Append($"    <loc>{loc}</loc>\n");
                xml.Append($"    <lastmod>{post.PublishedAtUtc:yyyy-MM-dd}</lastmod>\n");
                xml.Append("    <changefreq>monthly</changefreq>\n");
                xml.Append("    <priority>0.6</priority>\n");
                xml.Append("  </url>\n");
            }
            xml.Append("</urlset>\n");
            return Content(xml.ToString(), "application/xml; charset=utf-8");
        }

        [HttpGet("{slug}")]
        public async Task<ActionResult<BlogPostDetailDto>> GetBySlug(string slug, CancellationToken cancellationToken)
        {
            return Ok(await _blogService.GetPublishedBySlugAsync(slug, cancellationToken));
        }
    }

    /// <summary>Admin blog editor — full CRUD, published or not.</summary>
    [ApiController]
    [Route("api/blog/admin")]
    public class BlogAdminController : ControllerBase
    {
        private readonly IBlogService _blogService;

        public BlogAdminController(IBlogService blogService)
        {
            _blogService = blogService;
        }

        [HttpGet]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<IReadOnlyList<BlogPostDto>>> List(CancellationToken cancellationToken)
        {
            return Ok(await _blogService.ListAllAsync(cancellationToken));
        }

        [HttpGet("{id:guid}")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<BlogPostDto>> Get(Guid id, CancellationToken cancellationToken)
        {
            return Ok(await _blogService.GetAsync(id, cancellationToken));
        }

        [HttpPost]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<BlogPostDto>> Create(CreateBlogPostRequest request, CancellationToken cancellationToken)
        {
            var created = await _blogService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }

        [HttpPut("{id:guid}")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<BlogPostDto>> Update(Guid id, UpdateBlogPostRequest request, CancellationToken cancellationToken)
        {
            return Ok(await _blogService.UpdateAsync(id, request, cancellationToken));
        }

        [HttpDelete("{id:guid}")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        {
            await _blogService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
    }
}
