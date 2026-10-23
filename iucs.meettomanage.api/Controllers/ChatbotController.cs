using System.Security.Claims;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.application.Dto.Communication;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>
    /// The "Ask a Doubt" chatbot: every signed-in role can ask and browse their own history
    /// (mirrors FloatingNotesController's self-service shape); FAQ content management and
    /// teacher escalation triage are gated separately below.
    /// </summary>
    [ApiController]
    [Route("api/chatbot")]
    [Authorize]
    public class ChatbotController : ControllerBase
    {
        private readonly IChatbotService _chatbot;

        public ChatbotController(IChatbotService chatbot)
        {
            _chatbot = chatbot;
        }

        [HttpGet("faqs")]
        public async Task<ActionResult<IReadOnlyList<ChatFaqDto>>> ActiveFaqs(CancellationToken cancellationToken)
        {
            return Ok(await _chatbot.ListActiveFaqsAsync(cancellationToken));
        }

        [HttpPost("ask")]
        public async Task<ActionResult<AskChatbotResponse>> Ask(AskChatbotRequest request, CancellationToken cancellationToken)
        {
            return Ok(await _chatbot.AskAsync(UserId(), request, cancellationToken));
        }

        [HttpGet("history")]
        public async Task<ActionResult<IReadOnlyList<ChatMessageDto>>> History(CancellationToken cancellationToken)
        {
            return Ok(await _chatbot.ListMyMessagesAsync(UserId(), cancellationToken));
        }

        [HttpPut("messages/{id:guid}/feedback")]
        public async Task<ActionResult<ChatMessageDto>> SubmitFeedback(Guid id, SubmitChatFeedbackRequest request, CancellationToken cancellationToken)
        {
            return Ok(await _chatbot.SubmitFeedbackAsync(UserId(), id, request, cancellationToken));
        }

        // FAQ content management — same audience as Email Templates/Progress Reports.
        // AdmissionTeam included deliberately alongside SubAdmin: /admission/chatbot already
        // exists in the menu system for any admin who grants Communication to an admission
        // account — same gap already fixed on EmailTemplatesController/ProgressReportsController.
        [HttpGet("admin/faqs")]
        [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)},{nameof(UserRole.AdmissionTeam)}")]
        [HasPermission(PermissionModule.Communication, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<ChatFaqDto>>> AllFaqs(CancellationToken cancellationToken)
        {
            return Ok(await _chatbot.ListAllFaqsAsync(cancellationToken));
        }

        [HttpPost("admin/faqs")]
        [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)},{nameof(UserRole.AdmissionTeam)}")]
        [HasPermission(PermissionModule.Communication, PermissionAction.Create)]
        public async Task<ActionResult<ChatFaqDto>> CreateFaq(SaveChatFaqRequest request, CancellationToken cancellationToken)
        {
            var faq = await _chatbot.CreateFaqAsync(request, cancellationToken);
            return CreatedAtAction(nameof(AllFaqs), null, faq);
        }

        [HttpPut("admin/faqs/{id:guid}")]
        [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)},{nameof(UserRole.AdmissionTeam)}")]
        [HasPermission(PermissionModule.Communication, PermissionAction.Edit)]
        public async Task<ActionResult<ChatFaqDto>> UpdateFaq(Guid id, SaveChatFaqRequest request, CancellationToken cancellationToken)
        {
            return Ok(await _chatbot.UpdateFaqAsync(id, request, cancellationToken));
        }

        [HttpDelete("admin/faqs/{id:guid}")]
        [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)},{nameof(UserRole.AdmissionTeam)}")]
        [HasPermission(PermissionModule.Communication, PermissionAction.Delete)]
        public async Task<IActionResult> DeleteFaq(Guid id, CancellationToken cancellationToken)
        {
            await _chatbot.DeleteFaqAsync(id, cancellationToken);
            return NoContent();
        }

        // Escalation triage — Admin/SubAdmin/AdmissionTeam (the ChatbotAdmin.tsx screen calls
        // this unconditionally alongside the FAQ endpoints above) plus Teacher, who the doubt
        // actually gets routed to.
        [HttpGet("escalations")]
        [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)},{nameof(UserRole.AdmissionTeam)},{nameof(UserRole.Teacher)}")]
        [HasPermission(PermissionModule.Communication, PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<ChatEscalationDto>>> Escalations(
            [FromQuery] ChatEscalationStatus? status,
            CancellationToken cancellationToken)
        {
            return Ok(await _chatbot.ListEscalationsAsync(status, cancellationToken));
        }

        [HttpPut("escalations/{id:guid}/resolve")]
        [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)},{nameof(UserRole.AdmissionTeam)},{nameof(UserRole.Teacher)}")]
        [HasPermission(PermissionModule.Communication, PermissionAction.Edit)]
        public async Task<ActionResult<ChatEscalationDto>> ResolveEscalation(
            Guid id,
            ResolveChatEscalationRequest request,
            CancellationToken cancellationToken)
        {
            return Ok(await _chatbot.ResolveEscalationAsync(id, UserId(), request, cancellationToken));
        }

        // Usage analytics — same audience as the FAQ management endpoints above.
        [HttpGet("usage-stats")]
        [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.SubAdmin)},{nameof(UserRole.AdmissionTeam)}")]
        [HasPermission(PermissionModule.Communication, PermissionAction.View)]
        public async Task<ActionResult<ChatbotUsageStatsDto>> UsageStats(CancellationToken cancellationToken)
        {
            return Ok(await _chatbot.GetUsageStatsAsync(cancellationToken));
        }

        [HttpGet("admin/ai-configuration")]
        [Authorize(Roles = nameof(UserRole.Admin))]
        [HasPermission(PermissionModule.Settings, PermissionAction.View)]
        public async Task<ActionResult<AiChatConfigurationDto>> AiConfiguration(CancellationToken cancellationToken) =>
            Ok(await _chatbot.GetAiConfigurationAsync(cancellationToken));

        [HttpPut("admin/ai-configuration")]
        [Authorize(Roles = nameof(UserRole.Admin))]
        [HasPermission(PermissionModule.Settings, PermissionAction.Edit)]
        public async Task<ActionResult<AiChatConfigurationDto>> UpdateAiConfiguration(UpdateAiChatConfigurationRequest request, CancellationToken cancellationToken) =>
            Ok(await _chatbot.UpdateAiConfigurationAsync(request, cancellationToken));

        [HttpPost("admin/ai-configuration/test")]
        [Authorize(Roles = nameof(UserRole.Admin))]
        [HasPermission(PermissionModule.Settings, PermissionAction.Edit)]
        public async Task<ActionResult<TestAiChatResponse>> TestAi(TestAiChatRequest request, CancellationToken cancellationToken) =>
            Ok(await _chatbot.TestAiAsync(request, cancellationToken));

        [HttpGet("admin/ai-configuration/models")]
        [Authorize(Roles = nameof(UserRole.Admin))]
        [HasPermission(PermissionModule.Settings, PermissionAction.Edit)]
        public async Task<ActionResult<AiModelListDto>> AiModels(CancellationToken cancellationToken) =>
            Ok(await _chatbot.ListAiModelsAsync(cancellationToken));

        [HttpGet("admin/ai-cost-monitor")]
        [Authorize(Roles = nameof(UserRole.Admin))]
        [HasPermission(PermissionModule.Settings, PermissionAction.View)]
        public async Task<ActionResult<AiCostMonitorDto>> AiCostMonitor(CancellationToken cancellationToken) =>
            Ok(await _chatbot.GetAiCostMonitorAsync(cancellationToken));

        [HttpGet("business-brief")]
        [HasPermission(PermissionModule.ReportsAnalytics, PermissionAction.View)]
        public async Task<ActionResult<BusinessAiBriefDto>> BusinessBrief(CancellationToken cancellationToken) =>
            Ok(await _chatbot.GetBusinessBriefAsync(cancellationToken));

        [HttpPost("business-copilot")]
        [HasPermission(PermissionModule.ReportsAnalytics, PermissionAction.View)]
        public async Task<ActionResult<BusinessCopilotResponse>> BusinessCopilot(BusinessCopilotRequest request, CancellationToken cancellationToken) =>
            Ok(await _chatbot.AskBusinessCopilotAsync(request, cancellationToken));

        private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }
}
