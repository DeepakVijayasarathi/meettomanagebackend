using System.ComponentModel.DataAnnotations;

namespace iucs.readernest.application.Dto.Auth
{
    public class ForgotPinRequest
    {
        [Required]
        [EmailAddress]
        [MaxLength(256)]
        public string Email { get; set; } = null!;
    }
}
