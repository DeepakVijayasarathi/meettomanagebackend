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
                Source = request.Source,
                Medium = request.Medium,
                Campaign = request.Campaign,
                LandingPath = request.LandingPath,
                AssignedTo = request.AssignedTo,
                NextFollowUpAtUtc = request.NextFollowUpAtUtc,
                LastContactedAtUtc = request.LastContactedAtUtc,
                InternalNotes = request.InternalNotes,
                AssignedToUserId = request.AssignedToUserId,
                ArchivedAtUtc = request.ArchivedAtUtc,
                IsDuplicate = request.IsDuplicate,
                LeadScore = LeadScore(request),
                LeadTemperature = LeadScore(request) >= 70 ? "Hot" : LeadScore(request) >= 40 ? "Warm" : "Cold",
            };
        }

        private static int LeadScore(PlatformDemoRequest request)
        {
            var score = 20;
            if (!string.IsNullOrWhiteSpace(request.Message)) score += 15;
            if (request.Message?.StartsWith("Booked by the website chat assistant.") == true) score += 20;
            if (!string.IsNullOrWhiteSpace(request.Campaign)) score += 10;
            if (request.Status == iucs.meettomanage.domain.Enums.StoreInquiryStatus.Contacted) score += 15;
            if (request.Status == iucs.meettomanage.domain.Enums.StoreInquiryStatus.Converted) score = 100;
            if (request.Status == iucs.meettomanage.domain.Enums.StoreInquiryStatus.Closed) score = 0;
            score += request.EngagementScoreBoost;
            if (request.CreatedAtUtc > DateTime.UtcNow.AddDays(-3)) score += 10;
            return Math.Clamp(score, 0, 100);
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
                Status = !post.IsPublished ? "Draft" : post.PublishedAtUtc > DateTime.UtcNow ? "Scheduled" : "Published",
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
