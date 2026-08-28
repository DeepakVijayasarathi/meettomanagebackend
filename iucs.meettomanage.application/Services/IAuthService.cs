using iucs.meettomanage.application.Dto.Auth;
using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Services
{
    public interface IAuthService
    {
        Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

        Task<LoginResponse> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// The account's current role/status/permissions, read fresh from the database —
        /// deliberately not from anything baked into a JWT at login. Backs the per-request
        /// re-check in Program.cs's OnTokenValidated: a permission or role change (or a
        /// deactivation) now takes effect on that account's very next request instead of
        /// waiting for its token to expire or for the user to log in again.
        /// </summary>
        Task<CurrentAccessSnapshot?> GetCurrentAccessAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Emails a one-time reset link if the address has an account; always completes the
        /// same way either way (no signal to the caller about whether the address exists).
        /// </summary>
        Task RequestPinResetAsync(ForgotPinRequest request, CancellationToken cancellationToken = default);

        /// <summary>Redeems a reset-link token: sets the new PIN and burns the token.</summary>
        Task ResetPinAsync(ResetPinRequest request, CancellationToken cancellationToken = default);
    }
}
