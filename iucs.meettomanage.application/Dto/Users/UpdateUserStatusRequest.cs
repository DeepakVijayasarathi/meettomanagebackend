using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Dto.Users
{
    public class UpdateUserStatusRequest
    {
        [Required]
        public UserStatus Status { get; set; }
    }
}
