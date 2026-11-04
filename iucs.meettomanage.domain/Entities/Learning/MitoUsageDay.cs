using iucs.meettomanage.domain.Entities.Common;
using iucs.meettomanage.domain.Entities.Users;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.domain.Entities.Learning
{
    /// <summary>
    /// How much one student used the Mito learning companion on one (UTC) day. Counts only — no message
    /// content — used for the per-student daily cap and the admin usage dashboard.
    /// </summary>
    [Index(nameof(ChildId), nameof(Date), IsUnique = true)]
    [Index(nameof(Date))]
    public class MitoUsageDay : BaseEntity
    {
        public Guid ChildId { get; set; }

        public Child Child { get; set; } = null!;

        public DateOnly Date { get; set; }

        public int Requests { get; set; }

        public int DoubtRequests { get; set; }

        public int PlanRequests { get; set; }

        public int ReviewRequests { get; set; }

        public int QuizRequests { get; set; }
    }
}
