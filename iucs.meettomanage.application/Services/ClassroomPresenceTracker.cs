using System.Collections.Concurrent;
using iucs.meettomanage.application.Common.Interfaces;

namespace iucs.meettomanage.application.Services
{
    /// <summary>Singleton, in-memory. See IClassroomPresenceTracker for why this exists alongside ClassroomHub's own Rooms dictionary.</summary>
    public class ClassroomPresenceTracker : IClassroomPresenceTracker
    {
        private static string Prefix => Common.TenantExecution.Current is { } tenant ? tenant.Id.ToString("N") + ":" : "";
        // Without a bound tenant only un-prefixed (single-client) rooms match: an empty prefix must never read
        // every tenant's rooms. Tenant keys are "{id:N}:{sessionId}"; session ids are GUIDs and contain no ':'.
        private IEnumerable<ConcurrentDictionary<string, LivePresenceEntry>> TenantRooms
        {
            get
            {
                var prefix = Prefix;
                return _rooms.Where(x => prefix.Length == 0 ? !x.Key.Contains(':') : x.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(x => x.Value);
            }
        }
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, LivePresenceEntry>> _rooms = new();

        public void UserJoined(string sessionId, string connectionId, Guid userId, string name, string role)
        {
            var room = _rooms.GetOrAdd(Prefix + sessionId, _ => new ConcurrentDictionary<string, LivePresenceEntry>());
            room[connectionId] = new LivePresenceEntry(sessionId, userId, name, role, DateTime.UtcNow);
        }

        public void UserLeft(string sessionId, string connectionId)
        {
            if (_rooms.TryGetValue(Prefix + sessionId, out var room))
            {
                if (room.TryRemove(connectionId, out var left))
                {
                    _lastLeftUtc[Prefix + sessionId + "|" + left.Role] = DateTime.UtcNow;
                    PruneLastLeft();
                }
                if (room.IsEmpty)
                {
                    _rooms.TryRemove(Prefix + sessionId, out _);
                }
            }
        }

        // Distinct people, not raw connections -- one person open on two tabs/devices (a real,
        // observed case: a teacher with two browser tabs open to the same class) registers as
        // two connectionIds but must still count as one person, or every count downstream
        // (this KPI, GetLiveUsersAsync's per-session participant lists) overstates who's live.
        // Jibri's "recording observer" connection (see ClassroomHub.JoinSession) is excluded
        // from every reader below -- it's a robot, not a person, and would otherwise show up as
        // a phantom "Recording" participant in the admin's live-users dashboard.
        public int TotalConnectedUsers => TenantRooms.Sum(r => r.Values.Where(IsRealPerson).Select(v => v.UserId).Distinct().Count());

        public int ActiveClassCount => TenantRooms.Count(room => room.Values.Any(IsRealPerson));

        public IReadOnlyList<LivePresenceEntry> GetLiveConnections() =>
            TenantRooms.SelectMany(room => room.Values).Where(IsRealPerson).ToList();

        private static bool IsRealPerson(LivePresenceEntry entry) => entry.Role != "observer";

        // When each role was last connected to each session (tenant-prefixed key) -- see IsPresentOrRecentlyLeft.
        private readonly ConcurrentDictionary<string, DateTime> _lastLeftUtc = new();

        // Only the last few minutes ever matter; drop entries older than a day so this in-memory map
        // can't keep growing (a few entries per class) for as long as the API runs.
        private void PruneLastLeft()
        {
            if (_lastLeftUtc.Count < 500)
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var entry in _lastLeftUtc)
            {
                if (entry.Value < cutoff)
                {
                    _lastLeftUtc.TryRemove(entry.Key, out _);
                }
            }
        }

        public bool IsPresentOrRecentlyLeft(string sessionId, string role, TimeSpan grace)
        {
            if (_rooms.TryGetValue(Prefix + sessionId, out var room) && room.Values.Any(e => e.Role == role))
            {
                return true;
            }

            return _lastLeftUtc.TryGetValue(Prefix + sessionId + "|" + role, out var leftAt) && DateTime.UtcNow - leftAt <= grace;
        }
    }
}
