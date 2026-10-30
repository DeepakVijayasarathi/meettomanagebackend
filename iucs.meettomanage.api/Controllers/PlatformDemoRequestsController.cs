using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using iucs.meettomanage.application.Dto.Common;

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
        private readonly IMarketingChatLogService _chatLog;
        private readonly ILogger<MarketingController> _logger;

        public MarketingController(
            IPlatformDemoRequestService platformDemoRequestService,
            ISalesAssistant salesAssistant,
            IMarketingAnalyticsService analytics,
            IMarketingChatLogService chatLog,
            ILogger<MarketingController> logger)
        {
            _platformDemoRequestService = platformDemoRequestService;
            _salesAssistant = salesAssistant;
            _analytics = analytics;
            _chatLog = chatLog;
            _logger = logger;
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
            [FromQuery] DateOnly? from = null,
            [FromQuery] DateOnly? to = null,
            CancellationToken cancellationToken = default)
        {
            return Ok(await _analytics.GetDashboardAsync(days, tzOffsetMinutes, cancellationToken, from, to));
        }

        /// <summary>Hetzner AI analysis of the same anonymous, aggregate marketing dashboard data.</summary>
        [HttpGet("analytics/insights")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<MarketingAiInsightsDto>> AnalyticsInsights(
            [FromQuery] int days = 30,
            [FromQuery] int tzOffsetMinutes = 0,
            [FromQuery] DateOnly? from = null,
            [FromQuery] DateOnly? to = null,
            CancellationToken cancellationToken = default)
        {
            var dashboard = await _analytics.GetDashboardAsync(days, tzOffsetMinutes, cancellationToken, from, to);
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
            SalesChatResponse? response = null;
            try
            {
                response = await _salesAssistant.ReplyAsync(request.Messages, cancellationToken, request.Attribution);
                return Ok(response);
            }
            finally
            {
                // Saved for the marketing console's Chats page, even when the assistant failed
                // (the visitor's question is still worth seeing). Never breaks the chat itself.
                if (request.ConversationId is { } conversationId)
                {
                    try
                    {
                        await _chatLog.RecordTurnAsync(conversationId, request.Messages, response?.Reply, response?.BookedName, request.Attribution, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Couldn't save website chat turn");
                    }
                }
            }
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
        private readonly IMarketingOperationsService _marketingOperations;
        private readonly ISalesAssistant _salesAssistant;

        public PlatformDemoRequestsAdminController(IPlatformDemoRequestService platformDemoRequestService, IMarketingOperationsService marketingOperations, ISalesAssistant salesAssistant)
        {
            _platformDemoRequestService = platformDemoRequestService;
            _marketingOperations = marketingOperations;
            _salesAssistant = salesAssistant;
        }

        [HttpGet]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<IReadOnlyList<PlatformDemoRequestDto>>> List(
            [FromQuery] StoreInquiryStatus? status,
            CancellationToken cancellationToken)
        {
            return Ok(await _platformDemoRequestService.ListAsync(status, cancellationToken));
        }

        [HttpGet("paged")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<PagedResult<PlatformDemoRequestDto>>> ListPaged([FromQuery] MarketingLeadQuery query, CancellationToken cancellationToken) =>
            Ok(await _platformDemoRequestService.ListPagedAsync(query, cancellationToken));

        [HttpGet("{id:guid}")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<MarketingLeadDetailDto>> Get(Guid id, CancellationToken cancellationToken) =>
            Ok(await _platformDemoRequestService.GetAsync(id, cancellationToken));

        [HttpGet("{id:guid}/recommendation")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<MarketingLeadRecommendationDto>> Recommendation(Guid id, CancellationToken cancellationToken)
        {
            var detail = await _platformDemoRequestService.GetAsync(id, cancellationToken);
            return Ok(await _salesAssistant.RecommendLeadAsync(detail.Lead, cancellationToken));
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

        [HttpPost("bulk")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<IActionResult> Bulk(BulkMarketingLeadRequest request, CancellationToken cancellationToken)
        {
            await _platformDemoRequestService.BulkUpdateAsync(request, cancellationToken);
            return NoContent();
        }

        [HttpPost("{id:guid}/restore")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
        {
            await _platformDemoRequestService.RestoreAsync(id, cancellationToken);
            return NoContent();
        }

        [HttpGet("team")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<IReadOnlyList<MarketingTeamMemberDto>>> Team(CancellationToken cancellationToken) => Ok(await _marketingOperations.TeamAsync(cancellationToken));

        [HttpGet("campaigns")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<IReadOnlyList<MarketingCampaignBudgetDto>>> Campaigns(CancellationToken cancellationToken) => Ok(await _marketingOperations.ListCampaignsAsync(cancellationToken));

        [HttpPut("campaigns")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<MarketingCampaignBudgetDto>> SaveCampaign(MarketingCampaignBudgetDto request, CancellationToken cancellationToken) => Ok(await _marketingOperations.SaveCampaignAsync(request, cancellationToken));

        [HttpDelete("campaigns/{id:guid}")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<IActionResult> DeleteCampaign(Guid id, CancellationToken cancellationToken) { await _marketingOperations.DeleteCampaignAsync(id, cancellationToken); return NoContent(); }

        [HttpGet("settings")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<MarketingReportSettingDto>> Settings(CancellationToken cancellationToken) => Ok(await _marketingOperations.GetSettingsAsync(cancellationToken));

        [HttpPut("settings")]
        [Authorize(Roles = nameof(UserRole.Marketing))]
        public async Task<ActionResult<MarketingReportSettingDto>> SaveSettings(MarketingReportSettingDto request, CancellationToken cancellationToken) => Ok(await _marketingOperations.SaveSettingsAsync(request, cancellationToken));
    }
}
