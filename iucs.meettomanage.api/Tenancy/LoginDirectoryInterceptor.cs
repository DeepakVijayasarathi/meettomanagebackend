using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Tenancy;
using iucs.meettomanage.domain.Entities.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace iucs.meettomanage.api.Tenancy;

public sealed class LoginDirectoryInterceptor(LoginDirectory directory, ILogger<LoginDirectoryInterceptor> logger) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not MeetToManageDbContext db) return result;
        var users = db.ChangeTracker.Entries<User>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(x => x.Entity).ToList();
        if (users.Count == 0) return result;
        // EF assigns GUIDs when users enter the tracker. Audit/soft delete runs first.
        await directory.ReserveAsync(users, cancellationToken);
        foreach (var user in users)
            db.LoginDirectoryChanges.Add(new LoginDirectoryChange { UserId = user.Id });
        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context?.ChangeTracker.Entries<User>().Any(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) == true)
            throw new InvalidOperationException("Tenant user writes must use SaveChangesAsync to maintain the login directory.");
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        // An explicit transaction may still roll back. Its outbox becomes visible only
        // on commit and is drained by the background worker instead.
        if (eventData.Context?.Database.CurrentTransaction is not null) return result;
        try { await directory.FlushAsync(cancellationToken); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Login-directory synchronization deferred; durable outbox will retry.");
        }
        return result;
    }
}
