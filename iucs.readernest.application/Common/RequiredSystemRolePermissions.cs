using iucs.readernest.domain.Enums;

namespace iucs.readernest.application.Common
{
    /// <summary>
    /// Grants a system role must always carry, regardless of how its permissions were last
    /// edited. Single source of truth for two different places that both need it:
    /// DatabaseInitializer's startup backfill (heals an install that's missing one, including
    /// already-assigned Sub Admin users' own snapshot rows) and RoleService.UpdateAsync (stops
    /// the Roles &amp; Permissions screen's replace-all save from silently wiping one back out —
    /// confirmed live: the "management" role kept losing GET /api/courses access every time an
    /// admin saved that preset without the Courses box checked, and the startup-only backfill
    /// only healed it on the next process restart, not the next save).
    /// </summary>
    public static class RequiredSystemRolePermissions
    {
        public static readonly IReadOnlyList<RequiredGrant> All =
        [
            new("teacher", PermissionModule.Payouts, View: true),
            new("parent", PermissionModule.SessionCalendarManagement, View: true),
            new("parent", PermissionModule.ContentAccessManagement, View: true),
            new("parent", PermissionModule.BillingFinance, View: true),
            new("parent", PermissionModule.Communication, View: true),
            new("admission", PermissionModule.BillingFinance, View: true, Edit: true, Approve: true),
            // The Admission Dashboard's KPI tiles, conversion funnel and "Today & Upcoming
            // Demos" list all read GET /api/sessions, which is gated on this module (see
            // SessionsController.List's [HasPermission] — the [Authorize(Roles=...)] on that
            // endpoint already allows AdmissionTeam, but the permission check still 403's
            // without this). Without it those widgets show "Couldn't load this data" forever,
            // even on a real account with real demos. Confirmed live via network trace:
            // GET /api/sessions?fromUtc=...&toUtc=... → 403 for the admission role.
            new("admission", PermissionModule.SessionCalendarManagement, View: true),
            // /management/revenue's course-wise breakdown reads GET /api/courses, which is
            // gated on this module, not ReportsAnalytics — without it the page's own API call
            // 403's and silently renders "No records found, ₹0 total" instead of the real
            // figures shown by the chart above it.
            new("management", PermissionModule.CourseBatchManagement, View: true),
        ];

        public sealed record RequiredGrant(
            string RoleName,
            PermissionModule Module,
            bool View = false,
            bool Create = false,
            bool Edit = false,
            bool Delete = false,
            bool Approve = false);
    }
}
