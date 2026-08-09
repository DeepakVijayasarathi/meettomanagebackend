using iucs.readernest.application.Dto.Auth;

namespace iucs.readernest.application.Services
{
    public interface IAuthService
    {
        Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

        Task<LoginResponse> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Emails a one-time reset link if the address has an account; always completes the
        /// same way either way (no signal to the caller about whether the address exists).
        /// </summary>
        Task RequestPinResetAsync(ForgotPinRequest request, CancellationToken cancellationToken = default);

        /// <summary>Redeems a reset-link token: sets the new PIN and burns the token.</summary>
        Task ResetPinAsync(ResetPinRequest request, CancellationToken cancellationToken = default);
    }
}
