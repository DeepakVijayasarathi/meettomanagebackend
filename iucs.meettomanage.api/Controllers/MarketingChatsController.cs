using iucs.meettomanage.application.Dto.Common;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers
{
    /// <summary>Marketing console → Chats: what visitors asked the website's AI chat assistant.</summary>
    [ApiController]
    [Route("api/marketing/chats")]
    [Authorize(Roles = nameof(UserRole.Marketing))]
    public class MarketingChatsController(IMarketingChatLogService chatLog) : ControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<PagedResult<MarketingChatConversationDto>>> List(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            [FromQuery] string? search = null,
            [FromQuery] bool bookedOnly = false,
            CancellationToken cancellationToken = default) =>
            Ok(await chatLog.ListAsync(page, pageSize, search, bookedOnly, cancellationToken));

        [HttpGet("{id:guid}")]
        public async Task<ActionResult<MarketingChatDetailDto>> Get(Guid id, CancellationToken cancellationToken) =>
            Ok(await chatLog.GetAsync(id, cancellationToken));
    }
}
