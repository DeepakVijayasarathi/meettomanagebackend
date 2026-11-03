using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.api.Marketing;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.application.Common.Interfaces;
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
    [MaxLength(120)] public string? Region { get; set; }
    [MaxLength(40)] public string? Phone { get; set; }
    [MaxLength(120)] public string? Source { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public sealed class ProspectUpdate
{
    public OutreachProspectStatus? Status { get; set; }
    public OutreachPipelineStage? PipelineStage { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    [MaxLength(120)] public string? AssignedTo { get; set; }
    public DateTime? NextActionAtUtc { get; set; }
}

public sealed class ProspectNoteInput { [Required, MaxLength(10000)] public string Detail { get; set; } = ""; }
public sealed class ProspectTaskInput { [Required, MaxLength(200)] public string Title { get; set; } = ""; [MaxLength(120)] public string? AssignedTo { get; set; } public DateTime DueAtUtc { get; set; } }
public sealed class InboundReplyInput { [Required, MaxLength(256)] public string From { get; set; } = ""; [MaxLength(200)] public string? Subject { get; set; } [Required, MaxLength(10000)] public string Text { get; set; } = ""; [MaxLength(200)] public string? ExternalId { get; set; } }
public sealed class DraftMessageInput { [RegularExpression("^(first-contact|reply)$")] public string Purpose { get; set; } = "first-contact"; }
public sealed class CampaignAiBriefInput
{
    [Required, MaxLength(300)] public string Audience { get; set; } = "";
    [MaxLength(120)] public string? Region { get; set; }
    [Required, MaxLength(500)] public string Goal { get; set; } = "Book a 15-minute product demo";
    [MaxLength(500)] public string? Offer { get; set; }
    [RegularExpression("^(Warm|Professional|Direct)$")] public string Tone { get; set; } = "Warm";
}
public class SendReplyInput
{
    [Required, MaxLength(200)] public string Subject { get; set; } = "";
    [Required, MaxLength(10000)] public string Body { get; set; } = "";
}
public sealed class SendProspectMessageInput : SendReplyInput
{
    [RegularExpression("^(first-contact|reply)$")] public string Purpose { get; set; } = "first-contact";
}
public sealed class ReactivateProspectsInput { [Required, MaxLength(100)] public List<Guid> ProspectIds { get; set; } = []; }

public sealed class ProspectImport
{
    [Required, MaxLength(5000)] public List<ProspectInput> Rows { get; set; } = [];
    /// <summary>Applied to rows without their own source.</summary>
    [MaxLength(120)] public string? Source { get; set; }
}

public sealed class BulkProspectInput
{
    [Required, MinLength(1), MaxLength(500)] public List<Guid> ProspectIds { get; set; } = [];
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
    [MaxLength(120)] public string? Region { get; set; }
    [Range(0, 100)] public int? MinScore { get; set; }
    public List<OutreachPipelineStage> Stages { get; set; } = [];
}

/// <summary>Cold e-mail outreach for the marketing console: prospects, campaigns, sending status and unsubscribe.</summary>
[ApiController, Route("api/marketing/outreach")]
public sealed class MarketingOutreachController(MeetToManageDbContext db, OutreachService outreach, ISalesAssistant salesAssistant, IConfiguration configuration) : ControllerBase
{
    // ---------------------------------------------------------------- status

    [HttpGet("summary"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var settings = await outreach.GetSettingsAsync(ct);
        var counts = (await db.OutreachProspects.AsNoTracking().GroupBy(x => x.Status).Select(g => new { g.Key, count = g.Count() }).ToListAsync(ct))
            .Select(x => new { status = x.Key.ToString(), x.count });
        return Ok(new
        {
            ready = outreach.NotReadyReason is null,
            replySyncReady = !string.IsNullOrWhiteSpace(configuration["Outreach:InboundWebhookSecret"]),
            imapSyncReady = !string.IsNullOrWhiteSpace(configuration["Outreach:Imap:Username"]) && !string.IsNullOrWhiteSpace(configuration["Outreach:Imap:Password"]),
            notReadyReason = outreach.NotReadyReason,
            from = settings.From,
            replyTo = settings.ReplyTo,
            dailyLimit = settings.DailyLimit,
            sentToday = await outreach.SentTodayAsync(now, settings, ct),
            sendingHours = $"{settings.WindowStartHour}:00–{settings.WindowEndHour}:00 ({settings.TimeZoneId})",
            prospects = counts,
        });
    }

    /// <summary>Every cold e-mail setting (saved values, else the server's configuration, else the defaults).</summary>
    [HttpGet("settings"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> GetSettings(CancellationToken ct) => Ok(await outreach.GetSettingsAsync(ct));

    [HttpPut("settings"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> SaveSettings(OutreachSettings input, CancellationToken ct)
    {
        if (await outreach.SaveSettingsAsync(input, ct) is { } problem) throw new DomainValidationException(problem);
        return Ok(await outreach.GetSettingsAsync(ct));
    }

    // ---------------------------------------------------------------- prospects

    [HttpGet("prospects"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Prospects([FromQuery] OutreachProspectStatus? status, [FromQuery] string? search, [FromQuery] string? region, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 10, 200);
        var query = db.OutreachProspects.AsNoTracking();
        if (status is { } s) query = query.Where(x => x.Status == s);
        if (!string.IsNullOrWhiteSpace(region)) query = query.Where(x => x.Region == region.Trim());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Email.ToLower().Contains(term) || (x.FirstName != null && x.FirstName.ToLower().Contains(term))
                || (x.AcademyName != null && x.AcademyName.ToLower().Contains(term)) || (x.City != null && x.City.ToLower().Contains(term))
                || (x.Region != null && x.Region.ToLower().Contains(term)));
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Email, x.FirstName, x.AcademyName, x.City, x.Region, x.Phone, x.Source, x.Notes, x.LeadScore, x.ScoreReason, pipelineStage = x.PipelineStage.ToString(), x.AssignedTo, x.NextActionAtUtc, status = x.Status.ToString(), x.LastContactedAtUtc, x.CreatedAtUtc })
            .ToListAsync(ct);
        return Ok(new { total, page, pageSize, items });
    }

    [HttpPost("prospects"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> AddProspect(ProspectInput input, CancellationToken ct)
    {
        var result = await ImportAsync([input], null, ct);
        if (result.Added + result.Restored == 0) throw new DomainValidationException(result.Skipped.FirstOrDefault()?.Reason ?? "The prospect could not be added.");
        return Ok(result);
    }

    [HttpPost("prospects/import"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Import(ProspectImport input, CancellationToken ct) => Ok(await ImportAsync(input.Rows, input.Source, ct));

    public sealed record Skip(string Email, string Reason);
    public sealed record ImportResult(int Added, int Restored, List<Skip> Skipped);

    private async Task<ImportResult> ImportAsync(IReadOnlyList<ProspectInput> rows, string? source, CancellationToken ct)
    {
        var skipped = new List<Skip>();
        var emails = rows.Select(r => r.Email?.Trim().ToLowerInvariant() ?? "").ToList();
        // Including removed rows: an address that once unsubscribed must never come back in.
        var existing = (await db.OutreachProspects.IgnoreQueryFilters().Where(x => emails.Contains(x.Email)).ToListAsync(ct))
            .ToDictionary(x => x.Email);
        var seen = new HashSet<string>();
        var added = 0;
        var restored = 0;
        foreach (var row in rows)
        {
            var email = row.Email?.Trim().ToLowerInvariant() ?? "";
            if (!OutreachService.LooksLikeEmail(email)) { skipped.Add(new(row.Email ?? "", "Not a valid e-mail address")); continue; }
            if (!seen.Add(email)) { skipped.Add(new(email, "Listed twice in this import")); continue; }
            if (existing.TryGetValue(email, out var known))
            {
                if (known.Status == OutreachProspectStatus.Unsubscribed) { skipped.Add(new(email, "Unsubscribed earlier: will not be e-mailed again")); continue; }
                if (!known.IsDeleted) { skipped.Add(new(email, "Already a prospect")); continue; }
                known.IsDeleted = false; known.DeletedAtUtc = null; known.Status = OutreachProspectStatus.New; known.PipelineStage = OutreachPipelineStage.New;
                known.FirstName = Clean(row.FirstName, 120); known.AcademyName = Clean(row.AcademyName, 200); known.City = Clean(row.City, 120); known.Region = Clean(row.Region, 120);
                known.Phone = Clean(row.Phone, 40); known.Source = Clean(row.Source, 120) ?? Clean(source, 120); known.Notes = Clean(row.Notes, 1000); known.LeadScore = Score(row); known.ScoreReason = ScoreReason(row);
                restored++;
                continue;
            }
            db.OutreachProspects.Add(new OutreachProspect
            {
                Email = email, FirstName = Clean(row.FirstName, 120), AcademyName = Clean(row.AcademyName, 200), City = Clean(row.City, 120), Region = Clean(row.Region, 120),
                Phone = Clean(row.Phone, 40), Source = Clean(row.Source, 120) ?? Clean(source, 120), Notes = Clean(row.Notes, 1000), LeadScore = Score(row), ScoreReason = ScoreReason(row),
            });
            added++;
        }
        await db.SaveChangesAsync(ct);
        return new ImportResult(added, restored, skipped);
    }

    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static int Score(ProspectInput row) => Math.Min(100, 20 + (!string.IsNullOrWhiteSpace(row.AcademyName) ? 20 : 0) + (!string.IsNullOrWhiteSpace(row.Phone) ? 10 : 0) + (!string.IsNullOrWhiteSpace(row.Region) ? 10 : 0) + (!string.IsNullOrWhiteSpace(row.Source) ? 10 : 0) + ((row.Notes?.Contains("online", StringComparison.OrdinalIgnoreCase) ?? false) ? 15 : 0) + ((row.Notes?.Contains("multiple", StringComparison.OrdinalIgnoreCase) ?? false) ? 15 : 0));
    private static string ScoreReason(ProspectInput row) => string.Join(", ", new[] { !string.IsNullOrWhiteSpace(row.AcademyName) ? "academy identified" : null, !string.IsNullOrWhiteSpace(row.Phone) ? "phone available" : null, !string.IsNullOrWhiteSpace(row.Region) ? "region assigned" : null, row.Notes?.Contains("online", StringComparison.OrdinalIgnoreCase) == true ? "online classes" : null, row.Notes?.Contains("multiple", StringComparison.OrdinalIgnoreCase) == true ? "multi-location potential" : null }.Where(x => x != null));

    [HttpGet("regions"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Regions(CancellationToken ct) => Ok(await db.OutreachProspects.AsNoTracking()
        .Where(x => x.Region != null && x.Region != "").GroupBy(x => x.Region!).Select(g => new { region = g.Key, count = g.Count() })
        .OrderByDescending(x => x.count).ThenBy(x => x.region).ToListAsync(ct));

    [HttpGet("analytics"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Analytics(CancellationToken ct)
    {
        var rows = await db.OutreachProspects.AsNoTracking().GroupBy(x => x.Region ?? "Unassigned").Select(g => new
        {
            region = g.Key, total = g.Count(), newCount = g.Count(x => x.Status == OutreachProspectStatus.New),
            contacted = g.Count(x => x.Status == OutreachProspectStatus.Contacted), replied = g.Count(x => x.Status == OutreachProspectStatus.Replied),
            interested = g.Count(x => x.Status == OutreachProspectStatus.Interested), bounced = g.Count(x => x.Status == OutreachProspectStatus.Bounced)
        }).OrderByDescending(x => x.total).ToListAsync(ct);
        var totals = new { total = rows.Sum(x => x.total), contacted = rows.Sum(x => x.contacted), replied = rows.Sum(x => x.replied), interested = rows.Sum(x => x.interested), bounced = rows.Sum(x => x.bounced) };
        var pipeline = await db.OutreachProspects.AsNoTracking().GroupBy(x => x.PipelineStage).Select(g => new { stage = g.Key.ToString(), count = g.Count() }).OrderByDescending(x => x.count).ToListAsync(ct);
        var overdueTasks = await db.OutreachTasks.AsNoTracking().Where(x => x.CompletedAtUtc == null && x.DueAtUtc <= DateTime.UtcNow).OrderBy(x => x.DueAtUtc).Take(20).Select(x => new { x.Id, x.ProspectId, x.Title, x.AssignedTo, x.DueAtUtc, x.Prospect.Email, x.Prospect.AcademyName, x.Prospect.Region }).ToListAsync(ct);
        var recentReplies = await db.OutreachProspects.AsNoTracking()
            .Where(x => x.Status == OutreachProspectStatus.Replied || x.Status == OutreachProspectStatus.Interested)
            .OrderByDescending(x => x.UpdatedAtUtc ?? x.LastContactedAtUtc ?? x.CreatedAtUtc).Take(20)
            .Select(x => new { x.Id, x.Email, x.FirstName, x.AcademyName, x.City, x.Region, status = x.Status.ToString(), x.LastContactedAtUtc, repliedAtUtc = x.UpdatedAtUtc ?? x.LastContactedAtUtc ?? x.CreatedAtUtc })
            .ToListAsync(ct);
        return Ok(new { totals, regions = rows, pipeline, overdueTasks, recentReplies });
    }

    [HttpGet("analytics/insights"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> AnalyticsInsights(CancellationToken ct)
    {
        var evidence = await db.OutreachProspects.AsNoTracking().GroupBy(x => x.Region ?? "Unassigned").Select(g => new
        {
            region = g.Key, prospects = g.Count(), contacted = g.Count(x => x.Status == OutreachProspectStatus.Contacted),
            replied = g.Count(x => x.Status == OutreachProspectStatus.Replied), interested = g.Count(x => x.Status == OutreachProspectStatus.Interested),
            bounced = g.Count(x => x.Status == OutreachProspectStatus.Bounced)
        }).OrderByDescending(x => x.prospects).ToListAsync(ct);
        return Ok(await salesAssistant.AnalyzeOutreachAsync(evidence, ct));
    }

    [HttpGet("growth"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Growth(CancellationToken ct)
    {
        var prospects = await db.OutreachProspects.AsNoTracking().Select(x => new { x.Id, x.Email, x.AcademyName, x.Region, x.Source, x.Notes, x.Status, x.PipelineStage, x.LeadScore, x.LastContactedAtUtc, x.CreatedAtUtc }).ToListAsync(ct);
        var replies = await db.OutreachProspectActivities.AsNoTracking().Where(x => x.Type == OutreachActivityType.ReplyReceived).Select(x => new { x.ProspectId, x.Detail }).ToListAsync(ct);
        var sourcePerformance = prospects.GroupBy(x => string.IsNullOrWhiteSpace(x.Source) ? "Unknown" : x.Source!).Select(g => new
        {
            source = g.Key, leads = g.Count(), contacted = g.Count(x => x.Status != OutreachProspectStatus.New),
            replies = g.Count(x => x.Status is OutreachProspectStatus.Replied or OutreachProspectStatus.Interested),
            demos = g.Count(x => x.PipelineStage is OutreachPipelineStage.DemoBooked or OutreachPipelineStage.Trial or OutreachPipelineStage.Negotiation or OutreachPipelineStage.Won),
            won = g.Count(x => x.PipelineStage == OutreachPipelineStage.Won)
        }).OrderByDescending(x => x.won).ThenByDescending(x => x.replies).ThenByDescending(x => x.leads).ToList();
        static decimal Weight(OutreachPipelineStage stage) => stage switch { OutreachPipelineStage.Qualified => .1m, OutreachPipelineStage.Contacted => .15m, OutreachPipelineStage.Replied => .3m, OutreachPipelineStage.DemoBooked => .5m, OutreachPipelineStage.Trial => .65m, OutreachPipelineStage.Negotiation => .8m, OutreachPipelineStage.Won => 1m, _ => 0m };
        var averageValue = Math.Max(0, configuration.GetValue("Outreach:AverageCustomerValue", 12000m));
        var expectedWins = prospects.Sum(x => Weight(x.PipelineStage));
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var dormant = prospects.Where(x => x.Status is OutreachProspectStatus.Contacted or OutreachProspectStatus.Replied or OutreachProspectStatus.Interested && (x.LastContactedAtUtc ?? x.CreatedAtUtc) < cutoff && x.PipelineStage != OutreachPipelineStage.Lost && x.PipelineStage != OutreachPipelineStage.Won).OrderByDescending(x => x.LeadScore).Take(50).Select(x => new { x.Id, x.Email, x.AcademyName, x.Region, x.LeadScore, inactiveSinceUtc = x.LastContactedAtUtc ?? x.CreatedAtUtc }).ToList();
        string Segment(string? name, string? notes)
        {
            var text = $"{name} {notes}".ToLowerInvariant();
            return text.Contains("phonic") || text.Contains("reading") || text.Contains("story") ? "Reading & phonics" : text.Contains("abacus") ? "Abacus" : text.Contains("art") ? "Art & creative" : text.Contains("yoga") ? "Yoga & wellness" : text.Contains("online") ? "Online tutoring" : "Other academies";
        }
        var segments = prospects.GroupBy(x => Segment(x.AcademyName, x.Notes)).Select(g => new { segment = g.Key, leads = g.Count(), qualified = g.Count(x => x.LeadScore >= 60), replies = g.Count(x => x.Status is OutreachProspectStatus.Replied or OutreachProspectStatus.Interested) }).OrderByDescending(x => x.leads).ToList();
        var objectionText = replies.Select(x => x.Detail?.ToLowerInvariant() ?? "").ToList();
        var objections = new[] { new { name = "Price", count = objectionText.Count(x => x.Contains("price") || x.Contains("cost") || x.Contains("expensive")) }, new { name = "Timing", count = objectionText.Count(x => x.Contains("later") || x.Contains("busy") || x.Contains("not now")) }, new { name = "Existing software", count = objectionText.Count(x => x.Contains("already use") || x.Contains("existing software")) }, new { name = "Not interested", count = objectionText.Count(x => x.Contains("not interested") || x.Contains("no thanks")) } }.OrderByDescending(x => x.count).ToList();
        var deliverability = new { healthy = prospects.Count(x => x.Status is not OutreachProspectStatus.Bounced and not OutreachProspectStatus.Unsubscribed), bounced = prospects.Count(x => x.Status == OutreachProspectStatus.Bounced), unsubscribed = prospects.Count(x => x.Status == OutreachProspectStatus.Unsubscribed), incompleteProfiles = prospects.Count(x => string.IsNullOrWhiteSpace(x.AcademyName) || string.IsNullOrWhiteSpace(x.Region)) };
        var actions = new List<string>();
        if (dormant.Count > 0) actions.Add($"Reactivate {dormant.Count} high-potential leads inactive for 30+ days.");
        if (deliverability.bounced > 0) actions.Add($"Remove or correct {deliverability.bounced} bounced addresses before the next campaign.");
        if (sourcePerformance.FirstOrDefault() is { } best) actions.Add($"Prioritize {best.source}; it currently has the strongest measured source performance.");
        if (prospects.Any(x => string.IsNullOrWhiteSpace(x.Source))) actions.Add("Add source labels to unattributed leads before comparing campaign ROI.");
        return Ok(new { generatedAtUtc = DateTime.UtcNow, forecast = new { averageCustomerValue = averageValue, expectedWins = Math.Round(expectedWins, 1), weightedRevenue = Math.Round(expectedWins * averageValue, 0), currency = "INR" }, sourcePerformance, segments, objections, deliverability, dormant, recommendedActions = actions });
    }

    [HttpPost("growth/reactivate"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Reactivate(ReactivateProspectsInput input, CancellationToken ct)
    {
        var ids = input.ProspectIds.Distinct().Take(100).ToList();
        var prospects = await db.OutreachProspects.Where(x => ids.Contains(x.Id) && x.Status != OutreachProspectStatus.Unsubscribed && x.Status != OutreachProspectStatus.Bounced && x.Status != OutreachProspectStatus.NotInterested).ToListAsync(ct);
        var existingTasks = await db.OutreachTasks.Where(x => ids.Contains(x.ProspectId) && x.CompletedAtUtc == null).Select(x => x.ProspectId).ToListAsync(ct);
        foreach (var p in prospects)
        {
            p.NextActionAtUtc = DateTime.UtcNow;
            if (!existingTasks.Contains(p.Id)) db.OutreachTasks.Add(new OutreachTask { ProspectId = p.Id, Title = "Reactivate dormant lead with a personalized message", AssignedTo = p.AssignedTo, DueAtUtc = DateTime.UtcNow });
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { queued = prospects.Count });
    }

    [HttpGet("growth/ai-plan"), Authorize(Roles = nameof(UserRole.Marketing)), EnableRateLimiting("login")]
    public async Task<IActionResult> GrowthAiPlan(CancellationToken ct)
    {
        var prospects = await db.OutreachProspects.AsNoTracking().ToListAsync(ct);
        var sources = prospects.GroupBy(x => x.Source ?? "Unknown").Select(g => new { source = g.Key, leads = g.Count(), contacted = g.Count(x => x.Status != OutreachProspectStatus.New), replies = g.Count(x => x.Status is OutreachProspectStatus.Replied or OutreachProspectStatus.Interested), demos = g.Count(x => x.PipelineStage is OutreachPipelineStage.DemoBooked or OutreachPipelineStage.Trial or OutreachPipelineStage.Negotiation or OutreachPipelineStage.Won), won = g.Count(x => x.PipelineStage == OutreachPipelineStage.Won) }).ToList();
        var pipeline = prospects.GroupBy(x => x.PipelineStage).Select(g => new { stage = g.Key.ToString(), count = g.Count() }).ToList();
        var evidence = new
        {
            purpose = "Create a prioritized seven-day B2B sales and marketing plan. Recommend specific human-reviewed actions, not automatic sending.",
            totals = new { leads = prospects.Count, contacted = prospects.Count(x => x.Status != OutreachProspectStatus.New), replied = prospects.Count(x => x.Status is OutreachProspectStatus.Replied or OutreachProspectStatus.Interested), demos = prospects.Count(x => x.PipelineStage is OutreachPipelineStage.DemoBooked or OutreachPipelineStage.Trial or OutreachPipelineStage.Negotiation or OutreachPipelineStage.Won), won = prospects.Count(x => x.PipelineStage == OutreachPipelineStage.Won), bounced = prospects.Count(x => x.Status == OutreachProspectStatus.Bounced), dormant30Days = prospects.Count(x => x.LastContactedAtUtc < DateTime.UtcNow.AddDays(-30)) },
            sources, pipeline,
            constraints = new[] { "Protect unsubscribed and bounced contacts", "Do not infer revenue or causation", "Use small-sample caution", "Prioritize demos and positive replies", "No personally identifiable information is included" }
        };
        return Ok(await salesAssistant.AnalyzeOutreachAsync(evidence, ct));
    }

    [HttpGet("today"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Today(CancellationToken ct)
    {
        var hotLeads = await db.OutreachProspects.AsNoTracking().Where(x => x.LeadScore >= 60 && (x.PipelineStage == OutreachPipelineStage.New || x.PipelineStage == OutreachPipelineStage.Qualified)).OrderByDescending(x => x.LeadScore).Take(10).Select(x => new { x.Id, x.Email, x.FirstName, x.AcademyName, x.Region, x.LeadScore, pipelineStage = x.PipelineStage.ToString(), x.ScoreReason }).ToListAsync(ct);
        var replies = await db.OutreachProspects.AsNoTracking().Where(x => x.PipelineStage == OutreachPipelineStage.Replied || x.Status == OutreachProspectStatus.Interested).OrderByDescending(x => x.UpdatedAtUtc).Take(10).Select(x => new { x.Id, x.Email, x.FirstName, x.AcademyName, x.Region, x.LeadScore, pipelineStage = x.PipelineStage.ToString(), x.NextActionAtUtc }).ToListAsync(ct);
        var tasks = await db.OutreachTasks.AsNoTracking().Where(x => x.CompletedAtUtc == null).OrderBy(x => x.DueAtUtc).Take(20).Select(x => new { x.Id, x.ProspectId, x.Title, x.AssignedTo, x.DueAtUtc, x.Prospect.Email, x.Prospect.AcademyName, x.Prospect.Region }).ToListAsync(ct);
        return Ok(new { hotLeads, replies, tasks, generatedAtUtc = DateTime.UtcNow });
    }

    [HttpGet("replies"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Replies(CancellationToken ct)
    {
        var replies = await db.OutreachProspectActivities.AsNoTracking().Where(x => x.Type == OutreachActivityType.ReplyReceived).OrderByDescending(x => x.CreatedAtUtc).Take(100).Select(x => new { x.Id, x.ProspectId, x.Subject, x.Detail, x.CreatedAtUtc, x.Prospect.Email, x.Prospect.FirstName, x.Prospect.AcademyName, x.Prospect.Region, status = x.Prospect.Status.ToString(), pipelineStage = x.Prospect.PipelineStage.ToString() }).ToListAsync(ct);
        var prospectIds = replies.Select(x => x.ProspectId).Distinct().ToList();
        var handled = await db.OutreachProspectActivities.AsNoTracking().Where(x => prospectIds.Contains(x.ProspectId) && x.Type == OutreachActivityType.ReplyHandled).Select(x => new { x.ProspectId, x.CreatedAtUtc }).ToListAsync(ct);
        var items = replies.Select(x =>
        {
            var text = (x.Subject + " " + x.Detail).ToLowerInvariant();
            var intent = text.Contains("unsubscribe") || text.Contains("remove me") ? "Unsubscribe" : text.Contains("not interested") || text.Contains("no thanks") ? "NotInterested" : text.Contains("price") || text.Contains("cost") ? "Pricing" : text.Contains("demo") || text.Contains("call") || text.Contains("meeting") ? "DemoRequest" : text.Contains("interested") || text.Contains("yes") ? "Interested" : "Question";
            var handledAt = handled.Where(h => h.ProspectId == x.ProspectId && h.CreatedAtUtc > x.CreatedAtUtc).OrderBy(h => h.CreatedAtUtc).Select(h => (DateTime?)h.CreatedAtUtc).FirstOrDefault();
            var isHandled = handledAt != null;
            var ageHours = Math.Max(0, (DateTime.UtcNow - x.CreatedAtUtc).TotalHours);
            return new { x.Id, x.ProspectId, x.Subject, x.Detail, x.CreatedAtUtc, x.Email, x.FirstName, x.AcademyName, x.Region, x.status, x.pipelineStage, intent, urgency = !isHandled && ageHours >= 24 ? "High" : ageHours >= 8 ? "Normal" : "Low", handled = isHandled, ageHours = Math.Round(ageHours, 1), responseHours = handledAt is { } at ? Math.Round(Math.Max(0, (at - x.CreatedAtUtc).TotalHours), 1) : (double?)null };
        }).ToList();
        var responseTimes = items.Where(x => x.responseHours != null).Select(x => x.responseHours!.Value).ToList();
        return Ok(new { total = items.Count, needsResponse = items.Count(x => !x.handled), overdue = items.Count(x => !x.handled && x.ageHours >= 24), interested = items.Count(x => x.intent is "Interested" or "DemoRequest" or "Pricing"), averageResponseHours = responseTimes.Count == 0 ? (double?)null : Math.Round(responseTimes.Average(), 1), items });
    }

    [HttpPost("replies/{id:guid}/analyze"), Authorize(Roles = nameof(UserRole.Marketing)), EnableRateLimiting("login")]
    public async Task<IActionResult> AnalyzeReply(Guid id, CancellationToken ct)
    {
        var reply = await db.OutreachProspectActivities.AsNoTracking().Where(x => x.Id == id && x.Type == OutreachActivityType.ReplyReceived).Select(x => new { x.Subject, x.Detail, prospect = new { x.Prospect.FirstName, x.Prospect.AcademyName, x.Prospect.City, x.Prospect.Region, x.Prospect.Status, x.Prospect.PipelineStage } }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Reply", id);
        return Ok(await salesAssistant.AnalyzeOutreachReplyAsync(reply, ct));
    }

    [HttpPost("replies/{id:guid}/handled"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> MarkReplyHandled(Guid id, CancellationToken ct)
    {
        var reply = await db.OutreachProspectActivities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Type == OutreachActivityType.ReplyReceived, ct) ?? throw new NotFoundException("Reply", id);
        db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = reply.ProspectId, Type = OutreachActivityType.ReplyHandled, Detail = "Reply reviewed and handled.", CreatedBy = User.Identity?.Name });
        var p = await db.OutreachProspects.FirstAsync(x => x.Id == reply.ProspectId, ct); p.NextActionAtUtc = null;
        await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpGet("replies/{id:guid}/thread"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> ReplyThread(Guid id, CancellationToken ct)
    {
        var reply = await db.OutreachProspectActivities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Type == OutreachActivityType.ReplyReceived, ct) ?? throw new NotFoundException("Reply", id);
        var messages = await db.OutreachProspectActivities.AsNoTracking().Where(x => x.ProspectId == reply.ProspectId && (x.Type == OutreachActivityType.EmailSent || x.Type == OutreachActivityType.ReplyReceived || x.Type == OutreachActivityType.ReplySent)).OrderBy(x => x.CreatedAtUtc).Select(x => new { x.Id, direction = x.Type == OutreachActivityType.ReplyReceived ? "Inbound" : "Outbound", x.Subject, x.Detail, x.CreatedAtUtc, x.CreatedBy }).ToListAsync(ct);
        return Ok(messages);
    }

    [HttpPost("replies/{id:guid}/send"), Authorize(Roles = nameof(UserRole.Marketing)), EnableRateLimiting("login")]
    public async Task<IActionResult> SendReply(Guid id, SendReplyInput input, CancellationToken ct)
    {
        var reply = await db.OutreachProspectActivities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Type == OutreachActivityType.ReplyReceived, ct) ?? throw new NotFoundException("Reply", id);
        var prospect = await db.OutreachProspects.FirstAsync(x => x.Id == reply.ProspectId, ct);
        if (prospect.Status == OutreachProspectStatus.Unsubscribed) throw new DomainValidationException("This contact unsubscribed. A reply cannot be sent.");
        var error = await outreach.SendReplyAsync(prospect, input.Subject.Trim(), input.Body.Trim(), User.Identity?.Name, ct);
        if (error is not null) throw new ExternalServiceException(error);
        return NoContent();
    }

    [HttpPost("prospects/{id:guid}/draft"), Authorize(Roles = nameof(UserRole.Marketing)), EnableRateLimiting("login")]
    public async Task<IActionResult> DraftMessage(Guid id, DraftMessageInput input, CancellationToken ct)
    {
        var p = await db.OutreachProspects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Prospect", id);
        var latestReply = input.Purpose == "reply" ? await db.OutreachProspectActivities.AsNoTracking().Where(x => x.ProspectId == id && x.Type == OutreachActivityType.ReplyReceived).OrderByDescending(x => x.CreatedAtUtc).Select(x => new { x.Subject, x.Detail }).FirstOrDefaultAsync(ct) : null;
        var evidence = new { p.FirstName, p.AcademyName, p.City, p.Region, p.Source, p.Notes, p.LeadScore, p.ScoreReason, latestReply };
        return Ok(new { message = await salesAssistant.DraftOutreachMessageAsync(evidence, input.Purpose, ct) });
    }

    [HttpPost("prospects/{id:guid}/send"), Authorize(Roles = nameof(UserRole.Marketing)), EnableRateLimiting("login")]
    public async Task<IActionResult> SendProspectMessage(Guid id, SendProspectMessageInput input, CancellationToken ct)
    {
        var prospect = await db.OutreachProspects.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Prospect", id);
        if (prospect.Status is OutreachProspectStatus.Unsubscribed or OutreachProspectStatus.Bounced or OutreachProspectStatus.NotInterested)
            throw new DomainValidationException("This prospect is not contactable.");
        var error = await outreach.SendApprovedMessageAsync(prospect, input.Subject.Trim(), input.Body.Trim(), input.Purpose == "reply", User.Identity?.Name, ct);
        if (error is not null) throw new ExternalServiceException(error);
        return NoContent();
    }

    [HttpPost("campaigns/ai-draft"), Authorize(Roles = nameof(UserRole.Marketing)), EnableRateLimiting("login")]
    public async Task<IActionResult> DraftCampaign(CampaignAiBriefInput input, CancellationToken ct)
    {
        return Ok(await salesAssistant.DraftOutreachCampaignAsync(new
        {
            input.Audience, input.Region, input.Goal, input.Offer, input.Tone,
            product = "Meet to Manage: live classes, attendance, fee collection, admissions and parent communication in one academy-management platform"
        }, ct));
    }

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
            p.PipelineStage = status switch { OutreachProspectStatus.Replied => OutreachPipelineStage.Replied, OutreachProspectStatus.Interested => OutreachPipelineStage.Qualified, OutreachProspectStatus.NotInterested => OutreachPipelineStage.Lost, OutreachProspectStatus.Contacted => OutreachPipelineStage.Contacted, _ => p.PipelineStage };
            if (status == OutreachProspectStatus.Unsubscribed) p.UnsubscribedAtUtc ??= DateTime.UtcNow;
            db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = p.Id, Type = OutreachActivityType.StatusChanged, Detail = $"Status changed to {status}.", CreatedBy = User.Identity?.Name });
        }
        if (input.Notes is not null) p.Notes = Clean(input.Notes, 1000);
        if (input.PipelineStage is { } stage) p.PipelineStage = stage;
        if (input.AssignedTo is not null) p.AssignedTo = Clean(input.AssignedTo, 120);
        if (input.NextActionAtUtc is not null) p.NextActionAtUtc = input.NextActionAtUtc;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("prospects/{id:guid}"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> ProspectDetail(Guid id, CancellationToken ct)
    {
        var p = await db.OutreachProspects.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.Email, x.FirstName, x.AcademyName, x.City, x.Region, x.Phone, x.Source, x.Notes, x.LeadScore, x.ScoreReason, status = x.Status.ToString(), pipelineStage = x.PipelineStage.ToString(), x.AssignedTo, x.NextActionAtUtc, x.LastContactedAtUtc, x.CreatedAtUtc }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Prospect", id);
        var activities = await db.OutreachProspectActivities.AsNoTracking().Where(x => x.ProspectId == id).OrderByDescending(x => x.CreatedAtUtc).Select(x => new { x.Id, type = x.Type.ToString(), x.Subject, x.Detail, x.CreatedBy, x.CreatedAtUtc }).ToListAsync(ct);
        var tasks = await db.OutreachTasks.AsNoTracking().Where(x => x.ProspectId == id).OrderBy(x => x.CompletedAtUtc != null).ThenBy(x => x.DueAtUtc).Select(x => new { x.Id, x.Title, x.AssignedTo, x.DueAtUtc, x.CompletedAtUtc }).ToListAsync(ct);
        return Ok(new { prospect = p, activities, tasks });
    }

    [HttpPost("prospects/{id:guid}/notes"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> AddNote(Guid id, ProspectNoteInput input, CancellationToken ct)
    {
        if (!await db.OutreachProspects.AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Prospect", id);
        db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = id, Type = OutreachActivityType.Note, Detail = Clean(input.Detail, 10000), CreatedBy = User.Identity?.Name });
        await db.SaveChangesAsync(ct); return Ok();
    }

    [HttpPost("prospects/{id:guid}/tasks"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> AddTask(Guid id, ProspectTaskInput input, CancellationToken ct)
    {
        var p = await db.OutreachProspects.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Prospect", id);
        db.OutreachTasks.Add(new OutreachTask { ProspectId = id, Title = input.Title.Trim(), AssignedTo = Clean(input.AssignedTo, 120), DueAtUtc = input.DueAtUtc });
        p.NextActionAtUtc = p.NextActionAtUtc is null || input.DueAtUtc < p.NextActionAtUtc ? input.DueAtUtc : p.NextActionAtUtc;
        await db.SaveChangesAsync(ct); return Ok();
    }

    [HttpPost("tasks/{id:guid}/complete"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> CompleteTask(Guid id, CancellationToken ct)
    {
        var task = await db.OutreachTasks.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Outreach task", id);
        task.CompletedAtUtc ??= DateTime.UtcNow; await db.SaveChangesAsync(ct); return Ok();
    }

    [HttpPost("prospects/{id:guid}/rescore"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> Rescore(Guid id, CancellationToken ct)
    {
        var p = await db.OutreachProspects.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Prospect", id);
        var input = new ProspectInput { Email = p.Email, AcademyName = p.AcademyName, Phone = p.Phone, Region = p.Region, Source = p.Source, Notes = p.Notes };
        p.LeadScore = Score(input); p.ScoreReason = ScoreReason(input);
        var qualification = await salesAssistant.AnalyzeOutreachAsync(new { prospect = new { p.AcademyName, p.City, p.Region, p.Source, p.Notes, p.LeadScore, profileSignals = p.ScoreReason } }, ct);
        p.ScoreReason = qualification.Summary;
        db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = id, Type = OutreachActivityType.AiQualified, Detail = $"Lead score {p.LeadScore}. {qualification.Summary}\nNext: {string.Join(" ", qualification.NextActions)}", CreatedBy = User.Identity?.Name });
        await db.SaveChangesAsync(ct); return Ok(new { p.LeadScore, p.ScoreReason, qualification.AiPowered });
    }

    [HttpPost("inbound/reply"), AllowAnonymous]
    public async Task<IActionResult> InboundReply(InboundReplyInput input, CancellationToken ct)
    {
        var secret = configuration["Outreach:InboundWebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret) || Request.Headers["X-Outreach-Webhook-Secret"] != secret) return Unauthorized();
        var email = input.From.Trim().ToLowerInvariant();
        var p = await db.OutreachProspects.FirstOrDefaultAsync(x => x.Email == email, ct);
        if (p is null) return Ok(new { matched = false });
        if (!string.IsNullOrWhiteSpace(input.ExternalId) && await db.OutreachProspectActivities.AnyAsync(x => x.ExternalId == input.ExternalId, ct)) return Ok(new { matched = true, duplicate = true });
        var normalized = input.Text.ToLowerInvariant();
        var unsubscribe = normalized.Contains("unsubscribe") || normalized.Contains("remove me") || normalized.Contains("stop emailing");
        var negative = normalized.Contains("not interested") || normalized.Contains("no thanks") || normalized.Contains("do not contact");
        var positive = normalized.Contains("interested") || normalized.Contains("demo") || normalized.Contains("pricing") || normalized.Contains("price") || normalized.Contains("call me") || normalized.Contains("yes");
        p.Status = unsubscribe ? OutreachProspectStatus.Unsubscribed : negative ? OutreachProspectStatus.NotInterested : positive ? OutreachProspectStatus.Interested : OutreachProspectStatus.Replied;
        p.PipelineStage = negative || unsubscribe ? OutreachPipelineStage.Lost : OutreachPipelineStage.Replied;
        p.NextActionAtUtc = negative || unsubscribe ? null : DateTime.UtcNow.AddHours(24);
        if (unsubscribe) p.UnsubscribedAtUtc ??= DateTime.UtcNow;
        db.OutreachProspectActivities.Add(new OutreachProspectActivity { ProspectId = p.Id, Type = OutreachActivityType.ReplyReceived, Subject = Clean(input.Subject, 200), Detail = Clean(input.Text, 10000), ExternalId = Clean(input.ExternalId, 200), CreatedBy = "Inbound email" });
        if (!negative && !unsubscribe && !await db.OutreachTasks.AnyAsync(x => x.ProspectId == p.Id && x.CompletedAtUtc == null, ct))
            db.OutreachTasks.Add(new OutreachTask { ProspectId = p.Id, Title = positive ? "Respond to interested lead" : "Review inbound reply", DueAtUtc = DateTime.UtcNow.AddHours(4), AssignedTo = p.AssignedTo });
        await db.OutreachCampaignRecipients.Where(r => r.ProspectId == p.Id && r.Status == OutreachRecipientStatus.Scheduled).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, OutreachRecipientStatus.Stopped), ct);
        await db.SaveChangesAsync(ct); return Ok(new { matched = true, prospectId = p.Id });
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

    [HttpPost("prospects/bulk-delete"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> BulkDelete(BulkProspectInput input, CancellationToken ct)
    {
        var ids = input.ProspectIds.Distinct().ToList();
        var prospects = await db.OutreachProspects.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        db.OutreachProspects.RemoveRange(prospects);
        await db.OutreachCampaignRecipients.Where(r => ids.Contains(r.ProspectId) && r.Status == OutreachRecipientStatus.Scheduled)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, OutreachRecipientStatus.Stopped), ct);
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted = prospects.Count });
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
        ApplySteps(campaign, input.Steps, (await outreach.GetSettingsAsync(ct)).MinFollowUpDays);
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
        ApplySteps(campaign, input.Steps, (await outreach.GetSettingsAsync(ct)).MinFollowUpDays);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void ApplySteps(OutreachCampaign campaign, List<StepInput> steps, int minFollowUpDays)
    {
        if (steps.Count is < 1 or > 3) throw new DomainValidationException("A campaign has a first e-mail and up to two follow-ups.");
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (string.IsNullOrWhiteSpace(s.Subject) || string.IsNullOrWhiteSpace(s.Body)) throw new DomainValidationException($"E-mail {i + 1} needs a subject and a message.");
            // Through the navigation: a new campaign has no id until it is saved.
            campaign.Steps.Add(new OutreachCampaignStep { Order = i + 1, DelayDays = i == 0 ? 0 : Math.Max(minFollowUpDays, s.DelayDays), Subject = s.Subject.Trim(), Body = s.Body.Trim() });
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
        if (!string.IsNullOrWhiteSpace(input.Region)) query = query.Where(p => p.Region == input.Region.Trim());
        if (input.MinScore is { } minScore) query = query.Where(p => p.LeadScore >= minScore);
        if (input.Stages.Count > 0) query = query.Where(p => input.Stages.Contains(p.PipelineStage));
        var ids = await query.Select(p => p.Id).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var prospectId in ids)
            db.OutreachCampaignRecipients.Add(new OutreachCampaignRecipient { CampaignId = id, ProspectId = prospectId, NextStep = 1, NextSendAtUtc = now });
        await db.SaveChangesAsync(ct);
        return Ok(new { enrolled = ids.Count });
    }

    [HttpPost("campaigns/{id:guid}/preview"), Authorize(Roles = nameof(UserRole.Marketing))]
    public async Task<IActionResult> PreviewRecipients(Guid id, EnrollInput input, CancellationToken ct)
    {
        if (!await db.OutreachCampaigns.AnyAsync(c => c.Id == id, ct)) throw new NotFoundException("Campaign", id);
        var already = db.OutreachCampaignRecipients.Where(r => r.CampaignId == id).Select(r => r.ProspectId);
        var query = db.OutreachProspects.AsNoTracking().Where(p => !already.Contains(p.Id) && p.Status == OutreachProspectStatus.New);
        if (!string.IsNullOrWhiteSpace(input.Region)) query = query.Where(p => p.Region == input.Region.Trim());
        if (input.MinScore is { } minScore) query = query.Where(p => p.LeadScore >= minScore);
        if (input.Stages.Count > 0) query = query.Where(p => input.Stages.Contains(p.PipelineStage));
        var count = await query.CountAsync(ct);
        var sample = await query.OrderByDescending(p => p.LeadScore).Take(10).Select(p => new { p.Id, p.Email, p.AcademyName, p.Region, p.LeadScore }).ToListAsync(ct);
        return Ok(new { count, sample });
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
