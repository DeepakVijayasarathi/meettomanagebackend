using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
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
        [HasPermission(PermissionModule.Marketing, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<BlogPostDto>>> List(CancellationToken cancellationToken)
        {
            return Ok(await _blogService.ListAllAsync(cancellationToken));
        }

        [HttpGet("{id:guid}")]
        [HasPermission(PermissionModule.Marketing, PermissionAction.View)]
        public async Task<ActionResult<BlogPostDto>> Get(Guid id, CancellationToken cancellationToken)
        {
            return Ok(await _blogService.GetAsync(id, cancellationToken));
        }

        [HttpPost]
        [HasPermission(PermissionModule.Marketing, PermissionAction.Create)]
        public async Task<ActionResult<BlogPostDto>> Create(CreateBlogPostRequest request, CancellationToken cancellationToken)
        {
            var created = await _blogService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }

        [HttpPut("{id:guid}")]
        [HasPermission(PermissionModule.Marketing, PermissionAction.Edit)]
        public async Task<ActionResult<BlogPostDto>> Update(Guid id, UpdateBlogPostRequest request, CancellationToken cancellationToken)
        {
            return Ok(await _blogService.UpdateAsync(id, request, cancellationToken));
        }

        [HttpDelete("{id:guid}")]
        [HasPermission(PermissionModule.Marketing, PermissionAction.Delete)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        {
            await _blogService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
    }
}
