using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Marketing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Controllers;

/// <summary>
/// Cold e-mail sending was removed, but e-mails already delivered still carry unsubscribe links (and a one-click
/// List-Unsubscribe header). These two endpoints keep those links working and keep the opt-out on record, so the
/// address stays excluded if outreach is ever brought back. The prospect tables are kept for the same reason.
/// </summary>
[ApiController, Route("api/marketing/outreach")]
public sealed class OutreachUnsubscribeController(MeetToManageDbContext db) : ControllerBase
{
    [HttpGet("unsubscribe/{token:guid}"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> Unsubscribe(Guid token, CancellationToken ct)
    {
        var ok = await UnsubscribeAsync(token, ct);
        var message = ok ? "You're unsubscribed. We won't e-mail you again." : "This unsubscribe link isn't valid. If you keep getting e-mails, reply to one and we'll remove you.";
        return Content($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Unsubscribe</title></head>" +
            $"<body style=\"font-family:Arial,sans-serif;background:#fff6ee;color:#1c1917;display:flex;min-height:100vh;align-items:center;justify-content:center;margin:0\">" +
            $"<main style=\"background:#fff;border-radius:16px;padding:32px 28px;max-width:420px;text-align:center;box-shadow:0 10px 30px rgba(0,0,0,.08)\"><h1 style=\"font-size:20px;margin:0 0 8px\">Meet to Manage</h1><p style=\"margin:0;color:#57534e\">{message}</p></main></body></html>",
            "text/html");
    }

    /// <summary>RFC 8058 one-click unsubscribe, posted by the mail client from the List-Unsubscribe header.</summary>
    [HttpPost("unsubscribe/{token:guid}"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> UnsubscribeOneClick(Guid token, CancellationToken ct)
    {
        await UnsubscribeAsync(token, ct);
        return Ok();
    }

    private async Task<bool> UnsubscribeAsync(Guid token, CancellationToken ct)
    {
        if (token == Guid.Empty) return false;
        var prospect = await db.OutreachProspects.FirstOrDefaultAsync(x => x.UnsubscribeToken == token, ct);
        if (prospect is null) return false;
        if (prospect.Status != OutreachProspectStatus.Unsubscribed)
        {
            prospect.Status = OutreachProspectStatus.Unsubscribed;
            prospect.UnsubscribedAtUtc = DateTime.UtcNow;
            await db.OutreachCampaignRecipients.Where(r => r.ProspectId == prospect.Id && r.Status == OutreachRecipientStatus.Scheduled)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, OutreachRecipientStatus.Stopped), ct);
            await db.SaveChangesAsync(ct);
        }
        return true;
    }
}
