using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Dto.Batches
{
    public class UpdateBatchStatusRequest
    {
        [Required]
        public BatchStatus Status { get; set; }
    }
}
