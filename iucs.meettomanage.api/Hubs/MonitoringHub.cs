using iucs.meettomanage.api.Auth;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.SignalR;

namespace iucs.meettomanage.api.Hubs
{
    /// <summary>
    /// Push-only: MonitoringBroadcastService periodically sends every connected client the
    /// same "MonitoringUpdate" payload (the full MonitoringSummaryDto, unchanged from what
    /// GET /api/monitoring/summary already returns) — there's nothing for a client to invoke.
    /// Gated by the same SystemMonitoring:View permission as the REST endpoint, so anyone who
    /// can open a connection here is already allowed to see this data.
    /// </summary>
    [HasPermission(PermissionModule.SystemMonitoring, PermissionAction.View)]
    public class MonitoringHub : Hub
    {
        public static string TenantGroup => "monitoring-" + (iucs.meettomanage.application.Common.TenantExecution.Current?.Id.ToString("N") ?? "legacy");
        private readonly MonitoringConnectionTracker _tracker;

        public MonitoringHub(MonitoringConnectionTracker tracker)
        {
            _tracker = tracker;
        }

        public override async Task OnConnectedAsync()
        {
            _tracker.Increment();
            await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup);
            await base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            _tracker.Decrement();
            return base.OnDisconnectedAsync(exception);
        }
    }

    /// <summary>Lets MonitoringBroadcastService skip its Prometheus round-trip entirely when nobody has the monitoring page open.</summary>
    public class MonitoringConnectionTracker
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _counts = new();
        private static Guid TenantId => iucs.meettomanage.application.Common.TenantExecution.Current?.Id ?? Guid.Empty;

        public void Increment() => _counts.AddOrUpdate(TenantId, 1, (_, count) => count + 1);
        public void Decrement() => _counts.AddOrUpdate(TenantId, 0, (_, count) => Math.Max(0, count - 1));
        public bool HasConnections => _counts.TryGetValue(TenantId, out var count) && count > 0;
    }
}
