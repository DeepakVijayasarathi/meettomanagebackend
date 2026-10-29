using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Saas;

public sealed record UsageFigures(int ActiveStudents, int StaffCount, long StorageBytes, int SessionMinutesMonth,
    int SessionsMonth, int RecordingsMonth)
{
    public long StorageMb => (StorageBytes + 1024 * 1024 - 1) / (1024 * 1024);

    /// <summary>Current usage for an entitlement key, or null when the key is not metered.</summary>
    public long? For(string key) => key switch
    {
        EntitlementKeys.MaxStudents => ActiveStudents,
        EntitlementKeys.MaxStaff => StaffCount,
        EntitlementKeys.MaxStorageMb => StorageMb,
        EntitlementKeys.MaxSessionMinutesPerMonth => SessionMinutesMonth,
        EntitlementKeys.MaxSessionsPerMonth => SessionsMonth,
        EntitlementKeys.MaxRecordingsPerMonth => RecordingsMonth,
        _ => null,
    };
}

/// <summary>Measures a tenant's usage from its own database and stores the daily snapshots in the master database.</summary>
public sealed class SaasUsageService(IDbContextFactory<MasterDbContext> factory, TenantScopeFactory scopes, ILogger<SaasUsageService> logger)
{
    private static readonly UserRole[] StaffRoles = [UserRole.Admin, UserRole.SubAdmin, UserRole.AdmissionTeam, UserRole.Teacher];

    public static readonly string[] MeteredKeys =
        [EntitlementKeys.MaxStudents, EntitlementKeys.MaxStaff, EntitlementKeys.MaxStorageMb,
            EntitlementKeys.MaxSessionMinutesPerMonth, EntitlementKeys.MaxSessionsPerMonth,
            EntitlementKeys.MaxRecordingsPerMonth];

    /// <summary>Runs inside an already-bound tenant scope (a request or <see cref="GetLiveAsync"/>).</summary>
    public static async Task<UsageFigures> ComputeAsync(MeetToManageDbContext db, DateTime now, CancellationToken ct)
    {
        var students = await db.Children.CountAsync(x => x.IsActive, ct);
        var staff = await db.Users.CountAsync(x => StaffRoles.Contains(x.Role) && x.Status != UserStatus.Inactive, ct);
        var storage = await db.Resources.SumAsync(x => x.FileSizeBytes ?? 0L, ct);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var sessions = await db.ClassSessions.AsNoTracking()
            .Where(x => x.ScheduledStartAtUtc >= monthStart && x.ScheduledStartAtUtc <= now
                && (x.Status == SessionStatus.Completed || x.Status == SessionStatus.InProgress))
            .Select(x => new { Start = x.ActualStartAtUtc ?? x.ScheduledStartAtUtc, End = x.ActualEndAtUtc ?? x.ScheduledEndAtUtc })
            .ToListAsync(ct);
        var minutes = (int)sessions.Sum(x => Math.Max(0, (x.End - x.Start).TotalMinutes));
        var nextMonth = monthStart.AddMonths(1);
        var sessionCount = await db.ClassSessions.CountAsync(x => x.ScheduledStartAtUtc >= monthStart
            && x.ScheduledStartAtUtc < nextMonth && !x.IsDeleted, ct);
        var recordingCount = await db.SessionRecordings.CountAsync(x => x.CreatedAtUtc >= monthStart
            && x.CreatedAtUtc < nextMonth && !x.IsDeleted, ct)
            + await db.PersonalRoomRecordings.CountAsync(x => x.CreatedAtUtc >= monthStart
                && x.CreatedAtUtc < nextMonth && !x.IsDeleted, ct);
        return new UsageFigures(students, staff, storage, minutes, sessionCount, recordingCount);
    }

    public async Task<UsageFigures> GetLiveAsync(Guid tenantId, CancellationToken ct)
    {
        await using var master = await factory.CreateDbContextAsync(ct);
        var tenant = await master.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new application.Common.Exceptions.NotFoundException("Tenant", tenantId);
        return await MeasureAsync(tenant, ct);
    }

    private async Task<UsageFigures> MeasureAsync(Tenant tenant, CancellationToken ct)
    {
        using var ambient = TenantExecution.Enter(tenant.Id, tenant.Code);
        using var scope = scopes.CreateScope(tenant);
        return await ComputeAsync(scope.ServiceProvider.GetRequiredService<MeetToManageDbContext>(), DateTime.UtcNow, ct);
    }

    /// <summary>Writes today's snapshot for every active tenant that does not have one yet.</summary>
    public async Task<IReadOnlyList<(Guid TenantId, UsageFigures Figures)>> SnapshotAllAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var master = await factory.CreateDbContextAsync(ct);
        var done = await master.TenantUsageSnapshots.AsNoTracking().Where(x => x.SnapshotDate == today).Select(x => x.TenantId).ToListAsync(ct);
        var tenants = await master.Tenants.AsNoTracking().Where(x => x.Status == TenantStatus.Active && !done.Contains(x.Id)).ToListAsync(ct);
        var results = new List<(Guid, UsageFigures)>();
        foreach (var tenant in tenants)
        {
            try
            {
                var figures = await MeasureAsync(tenant, ct);
                master.TenantUsageSnapshots.Add(new TenantUsageSnapshot
                {
                    TenantId = tenant.Id, SnapshotDate = today, ActiveStudents = figures.ActiveStudents, StaffCount = figures.StaffCount,
                    StorageBytes = figures.StorageBytes, SessionMinutesMonth = figures.SessionMinutesMonth,
                    SessionsMonth = figures.SessionsMonth, RecordingsMonth = figures.RecordingsMonth,
                });
                await master.SaveChangesAsync(ct);
                results.Add((tenant.Id, figures));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                master.ChangeTracker.Clear();
                logger.LogError(ex, "Usage snapshot failed for tenant {TenantId}; other tenants continue.", tenant.Id);
            }
        }
        return results;
    }
}
