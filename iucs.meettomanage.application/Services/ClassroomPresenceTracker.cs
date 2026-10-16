using System.Collections.Concurrent;
using iucs.meettomanage.application.Common.Interfaces;

namespace iucs.meettomanage.application.Services
{
    /// <summary>Singleton, in-memory. See IClassroomPresenceTracker for why this exists alongside ClassroomHub's own Rooms dictionary.</summary>
    public class ClassroomPresenceTracker : IClassroomPresenceTracker
    {
        private static string Prefix => Common.TenantExecution.Current is { } tenant ? tenant.Id.ToString("N") + ":" : "";
        private IEnumerable<ConcurrentDictionary<string, LivePresenceEntry>> TenantRooms => _rooms.Where(x => x.Key.StartsWith(Prefix, StringComparison.Ordinal)).Select(x => x.Value);
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
                room.TryRemove(connectionId, out _);
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
    }
}
