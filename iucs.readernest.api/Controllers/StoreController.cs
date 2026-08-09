using iucs.readernest.api.Auth;
using iucs.readernest.application.Dto.Admission;
using iucs.readernest.application.Services;
using iucs.readernest.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace iucs.readernest.api.Controllers
{
    /// <summary>
    /// Public course catalog and "Enroll now" inquiry form — no login required, deliberately:
    /// the whole point is a visitor who doesn't have an account yet. See StoreInquiriesController
    /// for the staff-facing follow-up queue this feeds.
    /// </summary>
    [ApiController]
    [Route("api/store")]
    public class StoreController : ControllerBase
    {
        private readonly IStoreService _storeService;

        public StoreController(IStoreService storeService)
        {
            _storeService = storeService;
        }

        [HttpGet("plans")]
        public async Task<ActionResult<IReadOnlyList<StorePlanDto>>> Plans(CancellationToken cancellationToken)
        {
            return Ok(await _storeService.ListPublicPlansAsync(cancellationToken));
        }

        [HttpPost("inquiries")]
        [EnableRateLimiting("store-inquiry")]
        public async Task<ActionResult<StoreInquiryDto>> CreateInquiry(
            CreateStoreInquiryRequest request,
            CancellationToken cancellationToken)
        {
            var inquiry = await _storeService.CreateInquiryAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Plans), null, inquiry);
        }
    }

    /// <summary>Admission-team follow-up queue for public store inquiries.</summary>
    [ApiController]
    [Route("api/store/inquiries")]
    [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)}")]
    public class StoreInquiriesController : ControllerBase
    {
        private readonly IStoreService _storeService;

        public StoreInquiriesController(IStoreService storeService)
        {
            _storeService = storeService;
        }

        [HttpGet]
        [HasPermission(PermissionModule.Admission, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<StoreInquiryDto>>> List(
            [FromQuery] StoreInquiryStatus? status,
            CancellationToken cancellationToken)
        {
            return Ok(await _storeService.ListInquiriesAsync(status, cancellationToken));
        }

        [HttpPut("{id:guid}/status")]
        [HasPermission(PermissionModule.Admission, PermissionAction.Edit)]
        public async Task<ActionResult<StoreInquiryDto>> UpdateStatus(
            Guid id,
            UpdateStoreInquiryStatusRequest request,
            CancellationToken cancellationToken)
        {
            return Ok(await _storeService.UpdateInquiryStatusAsync(id, request, cancellationToken));
        }
    }
}
