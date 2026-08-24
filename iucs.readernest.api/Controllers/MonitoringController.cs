using iucs.readernest.api.Auth;
using iucs.readernest.application.Dto.Monitoring;
using iucs.readernest.application.Services;
using iucs.readernest.domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace iucs.readernest.api.Controllers
{
    /// <summary>Infra health for both production servers — Admin's Server Monitoring page.</summary>
    [ApiController]
    [Route("api/monitoring")]
    public class MonitoringController : ControllerBase
    {
        private readonly IMonitoringService _monitoringService;

        public MonitoringController(IMonitoringService monitoringService)
        {
            _monitoringService = monitoringService;
        }

        [HttpGet("summary")]
        [HasPermission(PermissionModule.SystemMonitoring, PermissionAction.View)]
        public async Task<ActionResult<MonitoringSummaryDto>> GetSummary(CancellationToken cancellationToken)
        {
            return Ok(await _monitoringService.GetSummaryAsync(cancellationToken));
        }
    }
}
