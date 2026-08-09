using iucs.readernest.application.Dto.Admission;
using iucs.readernest.domain.Entities.Admission;
using iucs.readernest.domain.Entities.Billing;

namespace iucs.readernest.application.Mappings
{
    public static class StoreMappings
    {
        public static StorePlanDto ToStoreDto(this PackagePlan plan)
        {
            return new StorePlanDto
            {
                Id = plan.Id,
                Name = plan.Name,
                CourseName = plan.Course?.Name,
                BillingType = plan.BillingType,
                BillingCycle = plan.BillingCycle,
                Price = plan.Price,
                SessionsIncluded = plan.SessionsIncluded,
            };
        }

        /// <summary>Caller must have loaded PackagePlan (e.g. via Include) before mapping.</summary>
        public static StoreInquiryDto ToDto(this StoreInquiry inquiry)
        {
            return new StoreInquiryDto
            {
                Id = inquiry.Id,
                PackagePlanId = inquiry.PackagePlanId,
                PlanName = inquiry.PackagePlan.Name,
                ParentName = inquiry.ParentName,
                ParentEmail = inquiry.ParentEmail,
                ParentPhone = inquiry.ParentPhone,
                ChildName = inquiry.ChildName,
                ChildAge = inquiry.ChildAge,
                Notes = inquiry.Notes,
                Status = inquiry.Status,
                CreatedAtUtc = inquiry.CreatedAtUtc,
            };
        }
    }
}
