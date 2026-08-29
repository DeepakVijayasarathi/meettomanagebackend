using System.Text;
using System.Text.RegularExpressions;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Mappings;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.application.Services
{
    public partial class BlogService : IBlogService
    {
        // Average adult silent-reading speed — good enough for a "N min read" estimate,
        // not meant to be precise.
        private const int WordsPerMinute = 200;

        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;

        public BlogService(IUnitOfWork unitOfWork, IAuditLogService auditLog)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
        }

        public async Task<IReadOnlyList<BlogPostSummaryDto>> ListPublishedAsync(CancellationToken cancellationToken = default)
        {
            var posts = await _unitOfWork.Repository<BlogPost>().Query()
                .Where(p => p.IsPublished)
                .OrderByDescending(p => p.PublishedAtUtc)
                .ToListAsync(cancellationToken);
            return posts.Select(p => p.ToSummaryDto()).ToList();
        }

        public async Task<BlogPostDetailDto> GetPublishedBySlugAsync(string slug, CancellationToken cancellationToken = default)
        {
            var normalized = slug.Trim().ToLowerInvariant();
            var post = await _unitOfWork.Repository<BlogPost>()
                .FirstOrDefaultAsync(p => p.Slug == normalized && p.IsPublished, cancellationToken)
                ?? throw new NotFoundException($"No published post found at '{slug}'.");
            return post.ToDetailDto();
        }

        public async Task<IReadOnlyList<BlogPostDto>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            var posts = await _unitOfWork.Repository<BlogPost>().Query()
                .OrderByDescending(p => p.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            return posts.Select(p => p.ToDto()).ToList();
        }

        public async Task<BlogPostDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var post = await _unitOfWork.Repository<BlogPost>().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(BlogPost), id);
            return post.ToDto();
        }

        public async Task<BlogPostDto> CreateAsync(CreateBlogPostRequest request, CancellationToken cancellationToken = default)
        {
            var slug = await ResolveUniqueSlugAsync(
                string.IsNullOrWhiteSpace(request.Slug) ? request.Title : request.Slug,
                excludingId: null,
                cancellationToken);

            var post = new BlogPost
            {
                Title = request.Title.Trim(),
                Slug = slug,
                Excerpt = request.Excerpt.Trim(),
                Content = request.Content.Trim(),
                ReadMinutes = EstimateReadMinutes(request.Content),
                IsPublished = request.IsPublished,
                PublishedAtUtc = request.IsPublished ? DateTime.UtcNow : null,
            };
            await _unitOfWork.Repository<BlogPost>().AddAsync(post, cancellationToken);
            await _auditLog.StageAsync(AuditAction.Create, nameof(BlogPost), post.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return post.ToDto();
        }

        public async Task<BlogPostDto> UpdateAsync(Guid id, UpdateBlogPostRequest request, CancellationToken cancellationToken = default)
        {
            var post = await _unitOfWork.Repository<BlogPost>().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(BlogPost), id);

            var slug = await ResolveUniqueSlugAsync(request.Slug, excludingId: id, cancellationToken);

            post.Title = request.Title.Trim();
            post.Slug = slug;
            post.Excerpt = request.Excerpt.Trim();
            post.Content = request.Content.Trim();
            post.ReadMinutes = EstimateReadMinutes(request.Content);
            // First publish stamps PublishedAtUtc; unpublishing keeps it so a later republish
            // doesn't silently jump to the back of the "newest first" list.
            if (request.IsPublished && post.PublishedAtUtc is null)
            {
                post.PublishedAtUtc = DateTime.UtcNow;
            }
            post.IsPublished = request.IsPublished;

            await _auditLog.StageAsync(AuditAction.Update, nameof(BlogPost), post.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return post.ToDto();
        }

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var repository = _unitOfWork.Repository<BlogPost>();
            var post = await repository.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(BlogPost), id);

            repository.Remove(post);
            await _auditLog.StageAsync(AuditAction.Delete, nameof(BlogPost), id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        private async Task<string> ResolveUniqueSlugAsync(string source, Guid? excludingId, CancellationToken cancellationToken)
        {
            var baseSlug = Slugify(source);
            if (string.IsNullOrEmpty(baseSlug))
            {
                throw new DomainValidationException("Couldn't derive a URL slug from that title — try adding more letters.");
            }

            var repository = _unitOfWork.Repository<BlogPost>();
            var slug = baseSlug;
            var suffix = 2;
            while (await repository.ExistsAsync(p => p.Slug == slug && (excludingId == null || p.Id != excludingId), cancellationToken))
            {
                slug = $"{baseSlug}-{suffix}";
                suffix++;
            }

            return slug;
        }

        private static string Slugify(string source)
        {
            var lowered = source.Trim().ToLowerInvariant();
            var hyphenated = NonAlphanumeric().Replace(lowered, "-");
            return hyphenated.Trim('-');
        }

        private static int EstimateReadMinutes(string content)
        {
            var wordCount = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            return Math.Max(1, (int)Math.Ceiling(wordCount / (double)WordsPerMinute));
        }

        [GeneratedRegex("[^a-z0-9]+")]
        private static partial Regex NonAlphanumeric();
    }
}
