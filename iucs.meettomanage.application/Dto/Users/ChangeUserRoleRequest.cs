using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Dto.Users
{
    public class ChangeUserRoleRequest
    {
        [Required]
        public UserRole Role { get; set; }
    }
}
