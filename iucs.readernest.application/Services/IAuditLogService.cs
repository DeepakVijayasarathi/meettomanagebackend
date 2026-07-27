using iucs.readernest.application.Dto.Audit;
using iucs.readernest.application.Dto.Common;
using iucs.readernest.domain.Enums;

namespace iucs.readernest.application.Services
{
    /// <summary>
    /// Appends rows to the audit trail. Callers stage the entry; it is persisted
    /// with the caller's SaveChangesAsync so the action and its audit line commit together.
    /// </summary>
    public interface IAuditLogService
    {
        Task StageAsync(
            AuditAction action,
            string entityName,
            string? entityId = null,
            string? changesJson = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Paged, newest-first audit trail for the admin/sub-admin Audit Log screen.
        /// When <paramref name="restrictToActorId"/> is set, only that user's own actions are returned
        /// — used for callers without platform-wide Settings:View access.
        /// </summary>
        Task<PagedResult<AuditLogDto>> ListAsync(
            string? entityName,
            AuditAction? action,
            int page,
            int pageSize,
            Guid? restrictToActorId = null,
            CancellationToken cancellationToken = default);
    }
}
