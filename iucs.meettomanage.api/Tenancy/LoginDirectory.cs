using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Tenancy;
using iucs.meettomanage.domain.Entities.Users;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Tenancy;

/// <summary>
/// Tenant users remain authoritative. Reserve aliases before a local write, then
/// mirror credentials from committed users using the tenant's durable outbox.
/// Login always verifies the current tenant PIN and lockout, never a stale mirror.
/// </summary>
public sealed class LoginDirectory(IDbContextFactory<MasterDbContext> factory, TenantContext tenant)
{
    /// <summary>
    /// Rebuilds this tenant's slice of the shared registry from its users. Shared phone numbers cannot
    /// belong to two users, so a clashing phone is skipped (the user still logs in by email) and returned.
    /// Clashing emails still stop the rebuild.
    /// </summary>
    public async Task<IReadOnlyList<string>> RebuildAsync(CancellationToken ct)
    {
        await using var business = new MeetToManageDbContext(new DbContextOptionsBuilder<MeetToManageDbContext>()
            .UseNpgsql(tenant.RequireConnectionString()).Options);
        var users = await business.Users.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var ids = users.Select(x => x.Id).ToHashSet();
        await using (var master = await factory.CreateDbContextAsync(ct))
        {
            var accounts = await master.LoginAccounts.Where(x => x.TenantId == tenant.TenantId).ToListAsync(ct);
            var orphanIds = accounts.Where(x => !ids.Contains(x.UserId)).Select(x => x.UserId).ToList();
            foreach (var account in accounts.Where(x => orphanIds.Contains(x.UserId)))
            {
                account.IsDeleted = true;
                account.PinHash = "";
            }
            master.LoginIdentifiers.RemoveRange(await master.LoginIdentifiers.Where(x => orphanIds.Contains(x.UserId)).ToListAsync(ct));
            await master.SaveChangesAsync(ct);
        }
        var skipped = new List<string>();
        await ReserveAsync(users, ct, skipPhoneConflicts: true, skipped);
        business.LoginDirectoryChanges.AddRange(ids.Select(id => new LoginDirectoryChange { UserId = id }));
        await business.SaveChangesAsync(ct);
        while (await business.LoginDirectoryChanges.AnyAsync(ct)) await FlushAsync(ct);
        return skipped.Distinct().Order().ToList();
    }

    public static IReadOnlyList<string> Aliases(User user)
    {
        var aliases = new List<string>();
        if (!string.IsNullOrWhiteSpace(user.Email)) aliases.Add(TenantResolver.NormalizeIdentifier(user.Email));
        if (ParentLogin.NormalizePhone(user.Phone) is { } phone) aliases.Add("phone:" + phone);
        return aliases;
    }

    public async Task ReserveAsync(IEnumerable<User> users, CancellationToken ct,
        bool skipPhoneConflicts = false, ICollection<string>? skippedPhones = null)
    {
        await using var master = await factory.CreateDbContextAsync(ct);
        await using var transaction = await master.Database.BeginTransactionAsync(ct);
        foreach (var user in users.Where(x => !x.IsDeleted))
        {
            var account = await master.LoginAccounts.FindAsync([user.Id], ct);
            if (account is null)
            {
                account = new() { UserId = user.Id, TenantId = tenant.TenantId!.Value };
                master.LoginAccounts.Add(account);
            }
            if (account.TenantId != tenant.TenantId) throw new ConflictException("The user belongs to another tenant.");
            foreach (var key in Aliases(user))
            {
                var existing = await master.LoginIdentifiers.Include(x => x.Account)
                    .SingleOrDefaultAsync(x => x.Identifier == key, ct);
                if (existing is not null && existing.UserId != user.Id)
                {
                    if (!existing.Account.IsDeleted)
                    {
                        if (skipPhoneConflicts && key.StartsWith("phone:", StringComparison.Ordinal))
                        {
                            skippedPhones?.Add(key);
                            continue;
                        }
                        throw new ConflictException("The email or mobile number is already in use.");
                    }
                    master.LoginIdentifiers.Remove(existing);
                    await master.SaveChangesAsync(ct);
                    existing = null;
                }
                if (existing is null)
                    master.LoginIdentifiers.Add(new() { Identifier = key, Account = account });
            }
            // Make reservations visible to the next user in this batch too.
            // Database uniqueness still arbitrates concurrent API instances.
            try { await master.SaveChangesAsync(ct); }
            catch (DbUpdateException) { throw new ConflictException("The email or mobile number is already in use."); }
        }
        try
        {
            await master.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("The email or mobile number is already in use. Refresh and try again.");
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await using var business = new MeetToManageDbContext(new DbContextOptionsBuilder<MeetToManageDbContext>()
            .UseNpgsql(tenant.RequireConnectionString()).Options);
        await using var transaction = await business.Database.BeginTransactionAsync(ct);
        // Serialize readers/writers across API instances so an older snapshot cannot
        // overwrite a newer credential. The lock releases on rollback/crash as well.
        await business.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(714907321)", ct);
        var changes = await business.LoginDirectoryChanges.OrderBy(x => x.Id).Take(200).ToListAsync(ct);
        if (changes.Count == 0) return;
        await using var master = await factory.CreateDbContextAsync(ct);
        await using var masterTransaction = await master.Database.BeginTransactionAsync(ct);
        foreach (var id in changes.Select(x => x.UserId).Distinct())
        {
            var user = await business.Users.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            var account = await master.LoginAccounts.FindAsync([id], ct);
            if (account is null)
            {
                account = new() { UserId = id, TenantId = tenant.TenantId!.Value };
                master.LoginAccounts.Add(account);
            }
            if (account.TenantId != tenant.TenantId) throw new InvalidOperationException("Directory membership mismatch.");
            account.IsDeleted = user is null || user.IsDeleted;
            account.PinHash = user is null || user.IsDeleted ? "" : user.PinHash;
            var desired = user is null || user.IsDeleted ? [] : Aliases(user);
            var aliases = await master.LoginIdentifiers.Where(x => x.UserId == id).ToListAsync(ct);
            master.LoginIdentifiers.RemoveRange(aliases.Where(x => !desired.Contains(x.Identifier)));
            foreach (var key in desired.Except(aliases.Select(x => x.Identifier)))
            {
                // A phone number already registered to another user (kept by a tenant import) stays theirs.
                if (key.StartsWith("phone:", StringComparison.Ordinal)
                    && (master.LoginIdentifiers.Local.Any(x => x.Identifier == key && x.UserId != id)
                        || await master.LoginIdentifiers.AnyAsync(x => x.Identifier == key && x.UserId != id, ct)))
                    continue;
                master.LoginIdentifiers.Add(new() { Identifier = key, Account = account });
            }
        }
        await master.SaveChangesAsync(ct);
        await masterTransaction.CommitAsync(ct);
        business.LoginDirectoryChanges.RemoveRange(changes);
        await business.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
