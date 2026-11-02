using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.api.Marketing;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Controllers;

public sealed class ProspectInput
{
    [Required, MaxLength(256)] public string Email { get; set; } = "";
    [MaxLength(120)] public string? FirstName { get; set; }
    [MaxLength(200)] public string? AcademyName { get; set; }
    [MaxLength(120)] public string? City { get; set; }
    [MaxLength(40)] public string? Phone { get; set; }
    [MaxLength(120)] public string? Source { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public sealed class ProspectUpdate
{
    public OutreachProspectStatus? Status { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public sealed class ProspectImport
{
    [Required, MaxLength(5000)] public List<ProspectInput> Rows { get; set; } = [];
    /// <summary>Applied to rows without their own source.</summary>
    [MaxLength(120)] public string? Source { get; set; }
}

public sealed class StepInput
{
    [Range(0, 60)] public int DelayDays { get; set; }
    [Required, MaxLength(200)] public string Subject { get; set; } = "";
    [Required, MaxLength(10000)] public string Body { get; set; } = "";
}

public sealed class CampaignInput
{
    [Required, MaxLength(150)] public string Name { get; set; } = "";
    [Required, MinLength(1), MaxLength(3)] public List<StepInput> Steps { get; set; } = [];
}

public sealed class EnrollInput
{
    /// <summary>Specific prospects; when empty, every contactable prospect with status New who isn't in this campaign yet.</summary>
    public List<Guid> ProspectIds { get; set; } = [];
}

/// <summary>Cold e-mail outreach for the marketing console: prospects, campaigns, sending status and unsubscribe.</summary>
[ApiController, Route("api/marketing/outreach")]
public sealed class MarketingOutreachController(MeetToManageDbContext db, OutreachService outreach) : ControllerBase
{
    // ---------------------------------------------------------------- status

    [HttpGet("summary"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var counts = await db.OutreachProspects.AsNoTracking().GroupBy(x => x.Status).Select(g => new { status = g.Key.ToString(), count = g.Count() }).ToListAsync(ct);
        return Ok(new
        {
            ready = outreach.NotReadyReason is null,
            notReadyReason = outreach.NotReadyReason,
            from = outreach.From,
            replyTo = outreach.ReplyTo,
            dailyLimit = await outreach.GetDailyLimitAsync(ct),
            sentToday = await outreach.SentTodayAsync(now, ct),
            sendingHours = $"{OutreachService.WindowStartHour}:00–{OutreachService.WindowEndHour}:00 IST",
            prospects = counts,
        });
    }

    public sealed record LimitInput([Range(1, OutreachService.MaxDailyLimit)] int DailyLimit);

    [HttpPut("settings"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Settings(LimitInput input, CancellationToken ct)
    {
        await outreach.SetDailyLimitAsync(input.DailyLimit, ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- prospects

    [HttpGet("prospects"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Prospects([FromQuery] OutreachProspectStatus? status, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 10, 200);
        var query = db.OutreachProspects.AsNoTracking();
        if (status is { } s) query = query.Where(x => x.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Email.ToLower().Contains(term) || (x.FirstName != null && x.FirstName.ToLower().Contains(term))
                || (x.AcademyName != null && x.AcademyName.ToLower().Contains(term)) || (x.City != null && x.City.ToLower().Contains(term)));
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Email, x.FirstName, x.AcademyName, x.City, x.Phone, x.Source, x.Notes, status = x.Status.ToString(), x.LastContactedAtUtc, x.CreatedAtUtc })
            .ToListAsync(ct);
        return Ok(new { total, page, pageSize, items });
    }

    [HttpPost("prospects"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> AddProspect(ProspectInput input, CancellationToken ct)
    {
        var result = await ImportAsync([input], null, ct);
        if (result.Added == 0) throw new DomainValidationException(result.Skipped.FirstOrDefault()?.Reason ?? "The prospect could not be added.");
        return Ok(result);
    }

    [HttpPost("prospects/import"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Import(ProspectImport input, CancellationToken ct) => Ok(await ImportAsync(input.Rows, input.Source, ct));

    public sealed record Skip(string Email, string Reason);
    public sealed record ImportResult(int Added, List<Skip> Skipped);

    private async Task<ImportResult> ImportAsync(IReadOnlyList<ProspectInput> rows, string? source, CancellationToken ct)
    {
        var skipped = new List<Skip>();
        var emails = rows.Select(r => r.Email?.Trim().ToLowerInvariant() ?? "").ToList();
        // Including removed rows: an address that once unsubscribed must never come back in.
        var existing = (await db.OutreachProspects.IgnoreQueryFilters().AsNoTracking().Where(x => emails.Contains(x.Email)).Select(x => new { x.Email, x.Status, x.IsDeleted }).ToListAsync(ct))
            .ToDictionary(x => x.Email);
        var seen = new HashSet<string>();
        var added = 0;
        foreach (var row in rows)
        {
            var email = row.Email?.Trim().ToLowerInvariant() ?? "";
            if (!OutreachService.LooksLikeEmail(email)) { skipped.Add(new(row.Email ?? "", "Not a valid e-mail address")); continue; }
            if (!seen.Add(email)) { skipped.Add(new(email, "Listed twice in this import")); continue; }
            if (existing.TryGetValue(email, out var known))
            {
                skipped.Add(new(email, known.Status == OutreachProspectStatus.Unsubscribed ? "Unsubscribed earlier: will not be e-mailed again" : known.IsDeleted ? "Removed earlier" : "Already a prospect"));
                continue;
            }
            db.OutreachProspects.Add(new OutreachProspect
            {
                Email = email, FirstName = Clean(row.FirstName, 120), AcademyName = Clean(row.AcademyName, 200), City = Clean(row.City, 120),
                Phone = Clean(row.Phone, 40), Source = Clean(row.Source, 120) ?? Clean(source, 120), Notes = Clean(row.Notes, 1000),
            });
            added++;
        }
        await db.SaveChangesAsync(ct);
        return new ImportResult(added, skipped);
    }

    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    [HttpPut("prospects/{id:guid}"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> UpdateProspect(Guid id, ProspectUpdate input, CancellationToken ct)
    {
        var p = await db.OutreachProspects.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Prospect", id);
        if (input.Status is { } status)
        {
            // An unsubscribe is the person's own choice: the team cannot undo it.
            if (p.Status == OutreachProspectStatus.Unsubscribed && status != OutreachProspectStatus.Unsubscribed)
                throw new DomainValidationException("This person unsubscribed and can't be e-mailed again.");
            p.Status = status;
            if (status == OutreachProspectStatus.Unsubscribed) p.UnsubscribedAtUtc ??= DateTime.UtcNow;
        }
        if (input.Notes is not null) p.Notes = Clean(input.Notes, 1000);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("prospects/{id:guid}"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> DeleteProspect(Guid id, CancellationToken ct)
    {
        var p = await db.OutreachProspects.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Prospect", id);
        db.OutreachProspects.Remove(p);
        await db.OutreachCampaignRecipients.Where(r => r.ProspectId == id && r.Status == OutreachRecipientStatus.Scheduled)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, OutreachRecipientStatus.Stopped), ct);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- campaigns

    [HttpGet("campaigns"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Campaigns(CancellationToken ct)
    {
        var campaigns = await db.OutreachCampaigns.AsNoTracking().Include(c => c.Steps).OrderByDescending(c => c.CreatedAtUtc).ToListAsync(ct);
        var ids = campaigns.Select(c => c.Id).ToList();
        var recipients = await db.OutreachCampaignRecipients.AsNoTracking().Where(r => ids.Contains(r.CampaignId))
            .GroupBy(r => new { r.CampaignId, r.Status }).Select(g => new { g.Key.CampaignId, g.Key.Status, n = g.Count() }).ToListAsync(ct);
        var sends = await db.OutreachSends.AsNoTracking().Where(s => ids.Contains(s.CampaignId) && s.ProspectId != Guid.Empty)
            .GroupBy(s => new { s.CampaignId, s.Succeeded }).Select(g => new { g.Key.CampaignId, g.Key.Succeeded, n = g.Count() }).ToListAsync(ct);
        int R(Guid id, OutreachRecipientStatus s) => recipients.Where(x => x.CampaignId == id && x.Status == s).Sum(x => x.n);
        return Ok(campaigns.Select(c => new
        {
            c.Id, c.Name, status = c.Status.ToString(), c.CreatedAtUtc,
            steps = c.Steps.OrderBy(s => s.Order).Select(s => new { s.Order, s.DelayDays, s.Subject, s.Body }),
            enrolled = recipients.Where(x => x.CampaignId == c.Id).Sum(x => x.n),
            scheduled = R(c.Id, OutreachRecipientStatus.Scheduled),
            completed = R(c.Id, OutreachRecipientStatus.Completed),
            stopped = R(c.Id, OutreachRecipientStatus.Stopped),
            sent = sends.Where(x => x.CampaignId == c.Id && x.Succeeded).Sum(x => x.n),
            failed = sends.Where(x => x.CampaignId == c.Id && !x.Succeeded).Sum(x => x.n),
        }));
    }

    [HttpPost("campaigns"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> CreateCampaign(CampaignInput input, CancellationToken ct)
    {
        var campaign = new OutreachCampaign { Name = input.Name.Trim() };
        ApplySteps(campaign, input.Steps);
        db.OutreachCampaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        return Ok(new { campaign.Id });
    }

    [HttpPut("campaigns/{id:guid}"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> UpdateCampaign(Guid id, CampaignInput input, CancellationToken ct)
    {
        var campaign = await db.OutreachCampaigns.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Campaign", id);
        campaign.Name = input.Name.Trim();
        // Steps are replaced outright (a soft delete would keep the unique campaign/order slot taken).
        await db.OutreachCampaignSteps.IgnoreQueryFilters().Where(s => s.CampaignId == id).ExecuteDeleteAsync(ct);
        ApplySteps(campaign, input.Steps);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void ApplySteps(OutreachCampaign campaign, List<StepInput> steps)
    {
        if (steps.Count is < 1 or > 3) throw new DomainValidationException("A campaign has a first e-mail and up to two follow-ups.");
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (string.IsNullOrWhiteSpace(s.Subject) || string.IsNullOrWhiteSpace(s.Body)) throw new DomainValidationException($"E-mail {i + 1} needs a subject and a message.");
            // Through the navigation: a new campaign has no id until it is saved.
            campaign.Steps.Add(new OutreachCampaignStep { Order = i + 1, DelayDays = i == 0 ? 0 : Math.Max(1, s.DelayDays), Subject = s.Subject.Trim(), Body = s.Body.Trim() });
        }
    }

    [HttpPost("campaigns/{id:guid}/enroll"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Enroll(Guid id, EnrollInput input, CancellationToken ct)
    {
        if (!await db.OutreachCampaigns.AnyAsync(c => c.Id == id, ct)) throw new NotFoundException("Campaign", id);
        var already = db.OutreachCampaignRecipients.Where(r => r.CampaignId == id).Select(r => r.ProspectId);
        var query = db.OutreachProspects.Where(p => !already.Contains(p.Id));
        query = input.ProspectIds.Count > 0
            ? query.Where(p => input.ProspectIds.Contains(p.Id) && (p.Status == OutreachProspectStatus.New || p.Status == OutreachProspectStatus.Contacted))
            : query.Where(p => p.Status == OutreachProspectStatus.New);
        var ids = await query.Select(p => p.Id).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var prospectId in ids)
            db.OutreachCampaignRecipients.Add(new OutreachCampaignRecipient { CampaignId = id, ProspectId = prospectId, NextStep = 1, NextSendAtUtc = now });
        await db.SaveChangesAsync(ct);
        return Ok(new { enrolled = ids.Count });
    }

    public sealed record StatusInput(OutreachCampaignStatus Status);

    [HttpPost("campaigns/{id:guid}/status"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> SetStatus(Guid id, StatusInput input, CancellationToken ct)
    {
        var campaign = await db.OutreachCampaigns.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Campaign", id);
        if (input.Status == OutreachCampaignStatus.Active)
        {
            if (outreach.NotReadyReason is { } reason) throw new DomainValidationException("Sending isn't set up: " + reason);
            if (!await db.OutreachCampaignRecipients.AnyAsync(r => r.CampaignId == id && r.Status == OutreachRecipientStatus.Scheduled, ct))
                throw new DomainValidationException("Add prospects to this campaign before starting it.");
        }
        if (input.Status is not (OutreachCampaignStatus.Active or OutreachCampaignStatus.Paused))
            throw new DomainValidationException("A campaign can only be started or paused.");
        campaign.Status = input.Status;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    public sealed record TestInput([Required, MaxLength(256)] string Email, int Step = 1);

    [HttpPost("campaigns/{id:guid}/test"), Authorize(Roles = nameof(UserRole.Marketing)), EnableRateLimiting("login")]
    public async Task<IActionResult> Test(Guid id, TestInput input, CancellationToken ct)
    {
        if (!OutreachService.LooksLikeEmail(input.Email)) throw new DomainValidationException("Enter a valid e-mail address for the test.");
        var error = await outreach.SendTestAsync(id, input.Step, input.Email, ct);
        if (error is not null) throw new DomainValidationException("The test e-mail was not sent: " + error);
        return NoContent();
    }

    [HttpDelete("campaigns/{id:guid}"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> DeleteCampaign(Guid id, CancellationToken ct)
    {
        var campaign = await db.OutreachCampaigns.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Campaign", id);
        if (campaign.Status == OutreachCampaignStatus.Active) throw new DomainValidationException("Pause the campaign before deleting it.");
        db.OutreachCampaigns.Remove(campaign);
        await db.OutreachCampaignRecipients.Where(r => r.CampaignId == id && r.Status == OutreachRecipientStatus.Scheduled)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, OutreachRecipientStatus.Stopped), ct);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- unsubscribe (public, from the e-mail)

    /// <summary>The link in every e-mail. Unsubscribes at once and shows a short confirmation page.</summary>
    [HttpGet("unsubscribe/{token:guid}"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> Unsubscribe(Guid token, CancellationToken ct)
    {
        var ok = await outreach.UnsubscribeAsync(token, ct);
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
        await outreach.UnsubscribeAsync(token, ct);
        return Ok();
    }
}
