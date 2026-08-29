using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>
    /// Public "request a platform demo" — a prospective ACADEMY asking to see the Meet to
    /// Manage software itself, evaluating whether to deploy it for their own business.
    /// Distinct from DemoBookingsController/StoreController's demo-bookings, which are a
    /// free trial CLASS an existing academy's own parent books for their child.
    /// </summary>
    [ApiController]
    [Route("api/marketing")]
    public class MarketingController : ControllerBase
    {
        private readonly IPlatformDemoRequestService _platformDemoRequestService;

        public MarketingController(IPlatformDemoRequestService platformDemoRequestService)
        {
            _platformDemoRequestService = platformDemoRequestService;
        }

        [HttpPost("demo-requests")]
        [EnableRateLimiting("store-inquiry")]
        public async Task<ActionResult<PlatformDemoRequestDto>> RequestDemo(
            CreatePlatformDemoRequestRequest request,
            CancellationToken cancellationToken)
        {
            var created = await _platformDemoRequestService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(RequestDemo), new { id = created.Id }, created);
        }
    }

    /// <summary>Admin follow-up queue for public platform-demo requests.</summary>
    [ApiController]
    [Route("api/marketing/demo-requests")]
    public class PlatformDemoRequestsAdminController : ControllerBase
    {
        private readonly IPlatformDemoRequestService _platformDemoRequestService;

        public PlatformDemoRequestsAdminController(IPlatformDemoRequestService platformDemoRequestService)
        {
            _platformDemoRequestService = platformDemoRequestService;
        }

        [HttpGet]
        [HasPermission(PermissionModule.Marketing, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<PlatformDemoRequestDto>>> List(
            [FromQuery] StoreInquiryStatus? status,
            CancellationToken cancellationToken)
        {
            return Ok(await _platformDemoRequestService.ListAsync(status, cancellationToken));
        }

        [HttpPut("{id:guid}/status")]
        [HasPermission(PermissionModule.Marketing, PermissionAction.Edit)]
        public async Task<ActionResult<PlatformDemoRequestDto>> UpdateStatus(
            Guid id,
            UpdatePlatformDemoRequestStatusRequest request,
            CancellationToken cancellationToken)
        {
            return Ok(await _platformDemoRequestService.UpdateStatusAsync(id, request, cancellationToken));
        }
    }
}
