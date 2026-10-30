using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>
    /// Public "request a platform demo" — a prospective ACADEMY asking to see the Meet to
    /// Manage software itself, evaluating whether to deploy it for their own business.
    /// Distinct from DemoBookingsController/StoreController's demo-bookings, which are a
    /// free trial CLASS an existing academy's own parent books for their child.
    /// </summary>
    [ApiController]
    [Route("api/marketing")]
    public class MarketingController : ControllerBase
    {
        private readonly IPlatformDemoRequestService _platformDemoRequestService;
        private readonly ISalesAssistant _salesAssistant;
        private readonly IMarketingAnalyticsService _analytics;

        public MarketingController(
            IPlatformDemoRequestService platformDemoRequestService,
            ISalesAssistant salesAssistant,
            IMarketingAnalyticsService analytics)
        {
            _platformDemoRequestService = platformDemoRequestService;
            _salesAssistant = salesAssistant;
            _analytics = analytics;
        }

        /// <summary>Anonymous analytics beacon from the public marketing pages (page views, chat starts).</summary>
        [HttpPost("track")]
        [EnableRateLimiting("marketing-track")]
        public async Task<IActionResult> Track(TrackMarketingEventRequest request, CancellationToken cancellationToken)
        {
            await _analytics.TrackAsync(request, Request.Headers.UserAgent.ToString(), cancellationToken);
            return NoContent();
        }

        /// <summary>The marketing console's analytics dashboard. Marketing role only.</summary>
        [HttpGet("analytics")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<MarketingDashboardDto>> Analytics(
            [FromQuery] int days = 30,
            [FromQuery] int tzOffsetMinutes = 0,
            CancellationToken cancellationToken = default)
        {
            return Ok(await _analytics.GetDashboardAsync(days, tzOffsetMinutes, cancellationToken));
        }

        /// <summary>Hetzner AI analysis of the same anonymous, aggregate marketing dashboard data.</summary>
        [HttpGet("analytics/insights")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<MarketingAiInsightsDto>> AnalyticsInsights(
            [FromQuery] int days = 30,
            [FromQuery] int tzOffsetMinutes = 0,
            CancellationToken cancellationToken = default)
        {
            var dashboard = await _analytics.GetDashboardAsync(days, tzOffsetMinutes, cancellationToken);
            return Ok(await _salesAssistant.AnalyzeMarketingAsync(dashboard, cancellationToken));
        }

        [HttpPost("demo-requests")]
        [EnableRateLimiting("store-inquiry")]
        public async Task<ActionResult<PlatformDemoRequestDto>> RequestDemo(
            CreatePlatformDemoRequestRequest request,
            CancellationToken cancellationToken)
        {
            var created = await _platformDemoRequestService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(RequestDemo), new { id = created.Id }, created);
        }

        /// <summary>
        /// The marketing site's Hetzner AI assistant. It can also book a platform demo into the
        /// same queue as <see cref="RequestDemo"/>. Anonymous, so rate-limited per IP.
        /// </summary>
        [HttpPost("chat")]
        [EnableRateLimiting("marketing-chat")]
        public async Task<ActionResult<SalesChatResponse>> Chat(SalesChatRequest request, CancellationToken cancellationToken)
        {
            return Ok(await _salesAssistant.ReplyAsync(request.Messages, cancellationToken));
        }

        [HttpPost("translate")]
        [EnableRateLimiting("marketing-chat")]
        public async Task<ActionResult<MarketingTranslationResponse>> Translate(MarketingTranslationRequest request, CancellationToken cancellationToken)
        {
            var translations = await _salesAssistant.TranslateAsync(request.Texts, request.TargetLanguage, cancellationToken);
            return Ok(new MarketingTranslationResponse { Translations = translations });
        }
    }

    /// <summary>Admin follow-up queue for public platform-demo requests.</summary>
    [ApiController]
    [Route("api/marketing/demo-requests")]
    public class PlatformDemoRequestsAdminController : ControllerBase
    {
        private readonly IPlatformDemoRequestService _platformDemoRequestService;

        public PlatformDemoRequestsAdminController(IPlatformDemoRequestService platformDemoRequestService)
        {
            _platformDemoRequestService = platformDemoRequestService;
        }

        [HttpGet]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<IReadOnlyList<PlatformDemoRequestDto>>> List(
            [FromQuery] StoreInquiryStatus? status,
            CancellationToken cancellationToken)
        {
            return Ok(await _platformDemoRequestService.ListAsync(status, cancellationToken));
        }

        [HttpPut("{id:guid}/status")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<PlatformDemoRequestDto>> UpdateStatus(
            Guid id,
            UpdatePlatformDemoRequestStatusRequest request,
            CancellationToken cancellationToken)
        {
            return Ok(await _platformDemoRequestService.UpdateStatusAsync(id, request, cancellationToken));
        }

        [HttpDelete("{id:guid}")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        {
            await _platformDemoRequestService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }

        [HttpPut("{id:guid}")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<PlatformDemoRequestDto>> Update(Guid id, UpdatePlatformDemoRequestRequest request, CancellationToken cancellationToken) =>
            Ok(await _platformDemoRequestService.UpdateAsync(id, request, cancellationToken));
    }
}
