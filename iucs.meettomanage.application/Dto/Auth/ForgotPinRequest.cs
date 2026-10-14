using System.ComponentModel.DataAnnotations;

namespace iucs.meettomanage.application.Dto.Auth
{
    public class ForgotPinRequest
    {
        /// <summary>The account's email, or a parent's mobile number (see ParentLogin).</summary>
        [Required]
        [MaxLength(256)]
        public string Email { get; set; } = null!;
    }
}
