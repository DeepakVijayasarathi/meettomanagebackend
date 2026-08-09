using System.ComponentModel.DataAnnotations;

namespace iucs.readernest.application.Dto.Auth
{
    public class ResetPinRequest
    {
        [Required]
        [MaxLength(64)]
        public string Token { get; set; } = null!;

        [Required]
        [RegularExpression(@"^\d{4}$", ErrorMessage = "PIN must be exactly 4 digits.")]
        public string NewPin { get; set; } = null!;
    }
}
