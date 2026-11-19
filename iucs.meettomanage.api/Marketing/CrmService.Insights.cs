using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Entities.Settings;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Marketing;

/// <summary>
/// Monthly targets per person (deals won and new monthly revenue, with progress this month) and leads that have
/// sat too long in one stage.
/// </summary>
public sealed partial class CrmService
{
    private const string TargetPrefix = "crm.target.";
    private sealed record StoredTarget(int? Won, decimal? Value);

    /// <summary>The start of the current calendar month in the user's time zone, as UTC.</summary>
    private static DateTime LocalMonthStartUtc(DateTime nowUtc, int utcOffsetMinutes)
    {
        var local = nowUtc.AddMinutes(utcOffsetMinutes);
        return new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(-utcOffsetMinutes);
    }

    public async Task<CrmTargets> TargetsAsync(int utcOffsetMinutes, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = LocalMonthStartUtc(now, utcOffsetMinutes);
        var visible = await access.VisibleToAsync(ct);
        var team = (await TeamAsync(ct)).Where(m => m.Active && m.Role != CrmRole.Viewer && (visible is null || m.UserId == visible)).ToList();

        var stored = await db.AppSettings.AsNoTracking().Where(x => x.Key.StartsWith(TargetPrefix)).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        var won = await db.PlatformDemoRequests.AsNoTracking()
            .Where(x => x.ArchivedAtUtc == null && x.Stage == LeadStage.Customer && x.StageChangedAtUtc >= monthStart && x.AssignedToUserId != null)
            .Select(x => new { Owner = x.AssignedToUserId!.Value, x.ExpectedMonthlyValue }).ToListAsync(ct);

        var rows = team.Select(m =>
        {
            var t = Parse(stored.GetValueOrDefault(TargetPrefix + m.UserId.ToString("N")));
            var mine = won.Where(x => x.Owner == m.UserId).ToList();
            return new CrmTargetRow(m.UserId, m.Name, m.Role, t?.Won, t?.Value, mine.Count, mine.Sum(x => x.ExpectedMonthlyValue ?? 0));
        }).OrderByDescending(r => r.Won).ThenBy(r => r.Name).ToList();

        var local = now.AddMinutes(utcOffsetMinutes);
        var daysInMonth = DateTime.DaysInMonth(local.Year, local.Month);
        return new CrmTargets(local.ToString("yyyy-MM"), local.Day, daysInMonth, rows);
    }

    private static StoredTarget? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StoredTarget>(json); } catch (JsonException) { return null; }
    }

    public async Task SaveTargetAsync(Guid userId, SaveCrmTargetRequest request, CancellationToken ct)
    {
        await access.RequireManagerAsync(ct);
        if (!await db.Users.AnyAsync(x => x.Id == userId && x.Role == UserRole.Marketing, ct)) throw new NotFoundException("Team member", userId);
        if (request.Won is < 0 || request.Value is < 0) throw new DomainValidationException("Targets can't be negative.");

        var key = TargetPrefix + userId.ToString("N");
        var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (request.Won is null && request.Value is null)
        {
            if (row is not null) db.AppSettings.Remove(row);
        }
        else
        {
            var value = JsonSerializer.Serialize(new StoredTarget(request.Won, request.Value));
            if (row is null) db.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = key, Value = value, Category = SettingCategory.General, IsPublic = false });
            else row.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Open leads that have waited too long: new and not contacted after a day, contacted with no demo after a week,
    /// a demo whose time has passed without being marked done, and demos or trials that went quiet.
    /// </summary>
    public async Task<List<CrmStuckLead>> StuckAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var visible = await access.VisibleToAsync(ct);
        var names = await TeamNamesAsync(ct);
        var q = db.PlatformDemoRequests.AsNoTracking().Where(x => x.ArchivedAtUtc == null && x.Stage != LeadStage.Customer && x.Stage != LeadStage.Lost);
        if (visible is { } v) q = q.Where(x => x.AssignedToUserId == v || x.AssignedToUserId == null);
        var leads = await q.Select(x => new { x.Id, x.FullName, x.AcademyName, x.AssignedToUserId, x.Stage, x.StageChangedAtUtc, x.CreatedAtUtc, x.DemoAtUtc, x.LastContactedAtUtc, x.ExpectedMonthlyValue })
            .ToListAsync(ct);

        var result = new List<CrmStuckLead>();
        foreach (var l in leads)
        {
            var since = l.StageChangedAtUtc ?? l.CreatedAtUtc;
            // A logged call or message counts as movement, so a lead being worked isn't flagged.
            var quietSince = l.LastContactedAtUtc is { } c && c > since ? c : since;
            var days = (now - quietSince).TotalDays;
            string? reason = l.Stage switch
            {
                LeadStage.New when (now - l.CreatedAtUtc).TotalHours >= 24 => "Not contacted yet",
                LeadStage.Contacted when days >= CrmRules.StuckDays(LeadStage.Contacted) => "No demo booked",
                LeadStage.DemoBooked when l.DemoAtUtc is { } d && (now - d).TotalHours >= 24 => "Demo time passed, not marked done",
                LeadStage.DemoDone when days >= CrmRules.StuckDays(LeadStage.DemoDone) => "No trial started after the demo",
                LeadStage.Trial when days >= CrmRules.StuckDays(LeadStage.Trial) => "Trial not in use",
                LeadStage.ActiveTrial when days >= CrmRules.StuckDays(LeadStage.ActiveTrial) => "Trial not converted",
                _ => null,
            };
            if (reason is null) continue;
            var waited = l.Stage == LeadStage.New ? now - l.CreatedAtUtc : l.Stage == LeadStage.DemoBooked && l.DemoAtUtc is { } at ? now - at : now - quietSince;
            result.Add(new CrmStuckLead(l.Id, l.FullName, l.AcademyName, l.AssignedToUserId is { } o ? names.GetValueOrDefault(o) : null,
                l.Stage, reason, (int)Math.Floor(waited.TotalDays), l.ExpectedMonthlyValue));
        }
        return result.OrderByDescending(x => x.ExpectedMonthlyValue ?? 0).ThenByDescending(x => x.Days).ToList();
    }
}

public sealed record CrmTargetRow(Guid UserId, string Name, CrmRole Role, int? WonTarget, decimal? ValueTarget, int Won, decimal WonValue);
public sealed record CrmTargets(string Month, int DayOfMonth, int DaysInMonth, List<CrmTargetRow> Rows);
public sealed record SaveCrmTargetRequest([Range(0, 10000)] int? Won, [Range(0, 100000000)] decimal? Value);
public sealed record CrmStuckLead(Guid Id, string FullName, string AcademyName, string? Owner, LeadStage Stage, string Reason, int Days, decimal? ExpectedMonthlyValue);
