using System.Security.Claims;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Dto.Billing;
using iucs.meettomanage.application.Mappings;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace iucs.meettomanage.api.Controllers;

/// <summary>Promo codes: staff manage them; staff or the family apply one to an unpaid invoice.</summary>
[ApiController]
[Route("api/discount-codes")]
public class DiscountCodesController(IDiscountService discounts) : ControllerBase
{
    [HttpGet]
    [HasPermission(PermissionModule.BillingFinance, PermissionAction.View)]
    public async Task<ActionResult<IReadOnlyList<DiscountCodeDto>>> List(CancellationToken cancellationToken) =>
        Ok(await discounts.ListAsync(cancellationToken));

    [HttpPost]
    [HasPermission(PermissionModule.BillingFinance, PermissionAction.Edit)]
    public async Task<ActionResult<DiscountCodeDto>> Create(SaveDiscountCodeRequest request, CancellationToken cancellationToken) =>
        Ok(await discounts.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    [HasPermission(PermissionModule.BillingFinance, PermissionAction.Edit)]
    public async Task<ActionResult<DiscountCodeDto>> Update(Guid id, SaveDiscountCodeRequest request, CancellationToken cancellationToken) =>
        Ok(await discounts.UpdateAsync(id, request, cancellationToken));

    [HttpPost("invoices/{invoiceId:guid}")]
    [HasPermission(PermissionModule.BillingFinance, PermissionAction.Edit)]
    public async Task<ActionResult<InvoiceDto>> ApplyToInvoice(Guid invoiceId, ApplyDiscountRequest request, CancellationToken cancellationToken) =>
        Ok((await discounts.ApplyAsync(invoiceId, request.Code, null, cancellationToken)).ToDto());

    [HttpDelete("invoices/{invoiceId:guid}")]
    [HasPermission(PermissionModule.BillingFinance, PermissionAction.Edit)]
    public async Task<ActionResult<InvoiceDto>> RemoveFromInvoice(Guid invoiceId, CancellationToken cancellationToken) =>
        Ok((await discounts.RemoveAsync(invoiceId, cancellationToken)).ToDto());

    /// <summary>A family applies a promo code to one of their own unpaid invoices before paying.</summary>
    [HttpPost("mine/invoices/{invoiceId:guid}")]
    [Authorize(Roles = nameof(UserRole.Parent))]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<InvoiceDto>> ApplyToMyInvoice(Guid invoiceId, ApplyDiscountRequest request, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return Ok((await discounts.ApplyAsync(invoiceId, request.Code, userId, cancellationToken)).ToDto());
    }
}
