using System.Security.Claims;
using iucs.meettomanage.application.Dto.Auth;
using iucs.meettomanage.application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace iucs.meettomanage.api.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("login")]
        [AllowAnonymous]
        [EnableRateLimiting("login")]
        public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
        {
            return Ok(await _authService.LoginAsync(request, cancellationToken));
        }

        [HttpGet("me")]
        [Authorize]
        public async Task<ActionResult<LoginResponse>> Me(CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return Ok(await _authService.GetCurrentUserAsync(userId, cancellationToken));
        }

        /// <summary>
        /// Always responds the same way whether or not the address has an account — the
        /// frontend shows one fixed "if that email exists, we've sent a link" message either way.
        /// </summary>
        [HttpPost("forgot-pin")]
        [AllowAnonymous]
        [EnableRateLimiting("pin-reset")]
        public async Task<IActionResult> ForgotPin(ForgotPinRequest request, CancellationToken cancellationToken)
        {
            await _authService.RequestPinResetAsync(request, cancellationToken);
            return NoContent();
        }

        [HttpPost("reset-pin")]
        [AllowAnonymous]
        [EnableRateLimiting("pin-reset")]
        public async Task<IActionResult> ResetPin(ResetPinRequest request, CancellationToken cancellationToken)
        {
            await _authService.ResetPinAsync(request, cancellationToken);
            return NoContent();
        }
    }
}
