using iucs.meettomanage.application.Dto.Communication;
using iucs.meettomanage.domain.Entities.Communication;

namespace iucs.meettomanage.application.Mappings
{
    public static class ProgressReportMappings
    {
        /// <summary>Caller must have loaded Child.ParentProfile.User (e.g. via Include) before mapping.</summary>
        public static ProgressReportDto ToDto(this ProgressReport report)
        {
            return new ProgressReportDto
            {
                Id = report.Id,
                ChildId = report.ChildId,
                ChildName = $"{report.Child.FirstName} {report.Child.LastName}".Trim(),
                ParentUserId = report.Child.ParentProfile.User.Id,
                ParentName = $"{report.Child.ParentProfile.User.FirstName} {report.Child.ParentProfile.User.LastName}".Trim(),
                ParentEmail = report.Child.ParentProfile.User.Email,
                PeriodYear = report.PeriodYear,
                PeriodMonth = report.PeriodMonth,
                Status = report.Status,
                Content = report.Content,
                SentAtUtc = report.SentAtUtc,
                UpdatedAtUtc = report.UpdatedAtUtc,
            };
        }
    }
}
