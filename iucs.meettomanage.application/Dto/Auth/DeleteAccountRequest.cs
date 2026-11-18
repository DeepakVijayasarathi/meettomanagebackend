using System.ComponentModel.DataAnnotations;

namespace iucs.meettomanage.application.Dto.Auth
{
    /// <summary>
    /// A parent deleting their own account from the app. The PIN proves it is them, and the
    /// confirmation text guards against an accidental tap.
    /// </summary>
    public class DeleteAccountRequest
    {
        [Required]
        [RegularExpression(@"^\d{4}$", ErrorMessage = "PIN must be exactly 4 digits.")]
        public string CurrentPin { get; set; } = null!;
    }
}
