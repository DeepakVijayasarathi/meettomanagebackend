using iucs.meettomanage.domain.Entities.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace iucs.meettomanage.domain.Data.Configurations
{
    public class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
    {
        public void Configure(EntityTypeBuilder<Subscription> builder)
        {
            // DB-enforced backstop for CreateSubscriptionAsync's application-level duplicate
            // check, which is a check-then-insert with no locking: two concurrent requests for
            // the same child+plan can both pass the check before either commits. A child can
            // have at most one Active subscription per plan at a time; a custom filter opts
            // this index out of MeetToManageDbContext's automatic "AND is_deleted = FALSE" append
            // (that only fires when the index declares no filter of its own), so is_deleted is
            // folded in here by hand, matching what every other unique index gets automatically.
            builder.HasIndex(s => new { s.ChildId, s.PackagePlanId })
                .IsUnique()
                .HasFilter("\"status\" = 'Active' AND \"is_deleted\" = FALSE");
        }
    }
}
