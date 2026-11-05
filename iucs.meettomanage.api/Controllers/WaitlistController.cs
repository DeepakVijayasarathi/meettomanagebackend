using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers;

/// <summary>Queues for full batches. Seats that open are offered in order (hourly); staff enrol from here.</summary>
[ApiController]
[Route("api/waitlist")]
public class WaitlistController(IWaitlistService waitlist) : ControllerBase
{
    [HttpGet]
    [HasPermission(PermissionModule.Admission, PermissionAction.View)]
    public async Task<ActionResult<IReadOnlyList<WaitlistEntryDto>>> List([FromQuery] Guid? batchId, CancellationToken cancellationToken) =>
        Ok(await waitlist.ListAsync(batchId, cancellationToken));

    [HttpPost]
    [HasPermission(PermissionModule.Admission, PermissionAction.Edit)]
    public async Task<ActionResult<WaitlistEntryDto>> Add(AddToWaitlistRequest request, CancellationToken cancellationToken) =>
        Ok(await waitlist.AddAsync(request, cancellationToken));

    [HttpPost("{id:guid}/enroll")]
    [HasPermission(PermissionModule.Admission, PermissionAction.Edit)]
    public async Task<ActionResult<WaitlistEntryDto>> Enroll(Guid id, CancellationToken cancellationToken) =>
        Ok(await waitlist.EnrollAsync(id, cancellationToken));

    [HttpDelete("{id:guid}")]
    [HasPermission(PermissionModule.Admission, PermissionAction.Edit)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        await waitlist.RemoveAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Offers free seats to the next students now instead of waiting for the hourly run.</summary>
    [HttpPost("offer-open-seats")]
    [HasPermission(PermissionModule.Admission, PermissionAction.Edit)]
    public async Task<ActionResult<object>> OfferOpenSeats(CancellationToken cancellationToken) =>
        Ok(new { offered = await waitlist.OfferOpenSeatsAsync(cancellationToken) });
}
