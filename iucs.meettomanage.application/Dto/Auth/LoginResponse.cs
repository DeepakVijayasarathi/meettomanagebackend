using iucs.meettomanage.application.Dto.Users;

namespace iucs.meettomanage.application.Dto.Auth
{
    public class LoginResponse
    {
        public Guid? TenantId { get; set; }
        public string? TenantCode { get; set; }
        public string AccessToken { get; set; } = null!;

        public DateTime ExpiresAtUtc { get; set; }

        public UserDto User { get; set; } = null!;

        /// <summary>"Module:Action" grants for Sub Admins; empty for other roles (Admin holds all implicitly).</summary>
        public IReadOnlyList<string> Permissions { get; set; } = [];

        /// <summary>
        /// Route the frontend should navigate to right after login — the user's
        /// assigned role's configured default route, or the portal home for
        /// their account type if none is set.
        /// </summary>
        public string DefaultRoute { get; set; } = null!;

        /// <summary>
        /// True when the PIN was right but the account uses two-step sign-in: no session is issued yet. The client
        /// sends <see cref="TwoFactorChallenge"/> with an authenticator code to /api/auth/login/two-factor.
        /// </summary>
        public bool RequiresTwoFactor { get; set; }

        public string? TwoFactorChallenge { get; set; }
    }

    public class CompleteTwoFactorLoginRequest
    {
        [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(128)]
        public string Challenge { get; set; } = null!;

        /// <summary>A 6-digit authenticator code or a one-time recovery code.</summary>
        [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(32)]
        public string Code { get; set; } = null!;
    }

    public class TwoFactorCodeRequest
    {
        [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(32)]
        public string Code { get; set; } = null!;
    }

    public class TwoFactorStatusDto
    {
        public bool Enabled { get; set; }
        public DateTime? EnabledAtUtc { get; set; }
        public int RecoveryCodesLeft { get; set; }
        /// <summary>Two-step sign-in is offered to staff accounts; family accounts use the app's device lock instead.</summary>
        public bool Available { get; set; }
    }

    public class TwoFactorSetupDto
    {
        /// <summary>Base32 secret for manual entry in an authenticator app.</summary>
        public string Secret { get; set; } = null!;
        /// <summary>otpauth:// URI to render as a QR code.</summary>
        public string OtpAuthUri { get; set; } = null!;
    }

    public class TwoFactorRecoveryCodesDto
    {
        public IReadOnlyList<string> RecoveryCodes { get; set; } = [];
    }
}
