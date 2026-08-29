using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.domain.Entities.Marketing;

namespace iucs.meettomanage.application.Mappings
{
    public static class MarketingMappings
    {
        public static PlatformDemoRequestDto ToDto(this PlatformDemoRequest request)
        {
            return new PlatformDemoRequestDto
            {
                Id = request.Id,
                FullName = request.FullName,
                WorkEmail = request.WorkEmail,
                Phone = request.Phone,
                AcademyName = request.AcademyName,
                Message = request.Message,
                Status = request.Status,
                CreatedAtUtc = request.CreatedAtUtc,
            };
        }

        public static BlogPostDto ToDto(this BlogPost post)
        {
            return new BlogPostDto
            {
                Id = post.Id,
                Title = post.Title,
                Slug = post.Slug,
                Excerpt = post.Excerpt,
                Content = post.Content,
                ReadMinutes = post.ReadMinutes,
                IsPublished = post.IsPublished,
                PublishedAtUtc = post.PublishedAtUtc,
                CreatedAtUtc = post.CreatedAtUtc,
            };
        }

        /// <summary>Caller must only pass published posts — PublishedAtUtc is asserted non-null.</summary>
        public static BlogPostSummaryDto ToSummaryDto(this BlogPost post)
        {
            return new BlogPostSummaryDto
            {
                Id = post.Id,
                Title = post.Title,
                Slug = post.Slug,
                Excerpt = post.Excerpt,
                ReadMinutes = post.ReadMinutes,
                PublishedAtUtc = post.PublishedAtUtc!.Value,
            };
        }

        /// <summary>Caller must only pass published posts — PublishedAtUtc is asserted non-null.</summary>
        public static BlogPostDetailDto ToDetailDto(this BlogPost post)
        {
            return new BlogPostDetailDto
            {
                Title = post.Title,
                Slug = post.Slug,
                Excerpt = post.Excerpt,
                Content = post.Content,
                ReadMinutes = post.ReadMinutes,
                PublishedAtUtc = post.PublishedAtUtc!.Value,
            };
        }
    }
}
