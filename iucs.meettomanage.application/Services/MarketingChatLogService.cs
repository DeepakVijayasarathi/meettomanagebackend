using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Dto.Common;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.application.Services;

/// <summary>Saves the website AI chat's conversations and lists them for the marketing console.</summary>
public interface IMarketingChatLogService
{
    /// <summary>
    /// Records one chat turn: the visitor's newest message and the assistant's reply (null when
    /// the assistant failed). A conversation seen for the first time also keeps the earlier turns
    /// the browser sent with it.
    /// </summary>
    Task RecordTurnAsync(string conversationKey, IReadOnlyList<SalesChatTurn> conversation, string? reply, string? bookedName,
        MarketingLeadAttributionDto? attribution, CancellationToken cancellationToken = default);

    Task<PagedResult<MarketingChatConversationDto>> ListAsync(int page, int pageSize, string? search, bool bookedOnly, CancellationToken cancellationToken = default);

    Task<MarketingChatDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

public class MarketingChatLogService(IUnitOfWork unitOfWork) : IMarketingChatLogService
{
    private const int MaxText = 4000;

    public async Task RecordTurnAsync(string conversationKey, IReadOnlyList<SalesChatTurn> conversation, string? reply, string? bookedName,
        MarketingLeadAttributionDto? attribution, CancellationToken cancellationToken = default)
    {
        if (conversation.Count == 0) return;
        var now = DateTime.UtcNow;
        var conversations = unitOfWork.Repository<MarketingChatConversation>();
        var messages = unitOfWork.Repository<MarketingChatMessage>();
        var row = await conversations.FirstOrDefaultAsync(c => c.ConversationKey == conversationKey, cancellationToken);

        IEnumerable<SalesChatTurn> newTurns;
        if (row is null)
        {
            row = new MarketingChatConversation
            {
                Id = Guid.NewGuid(),
                ConversationKey = conversationKey,
                VisitorId = Clip(attribution?.VisitorId, 100),
                Source = Clip(attribution?.Source, 100),
                Medium = Clip(attribution?.Medium, 100),
                Campaign = Clip(attribution?.Campaign, 100),
                LandingPath = Clip(attribution?.LandingPath, 200),
                FirstMessage = Clip(conversation.FirstOrDefault(t => t.Role == "user")?.Text, 300) ?? "",
            };
            await conversations.AddAsync(row, cancellationToken);
            newTurns = conversation;
        }
        else
        {
            // The browser resends the whole (trimmed) conversation each time; only its last
            // visitor message is new.
            newTurns = conversation[^1].Role == "user" ? [conversation[^1]] : [];
        }

        foreach (var turn in newTurns)
        {
            await messages.AddAsync(new MarketingChatMessage { Id = Guid.NewGuid(), ConversationId = row.Id, Position = row.MessageCount++, Role = turn.Role, Text = Clip(turn.Text, MaxText) ?? "" }, cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(reply))
        {
            await messages.AddAsync(new MarketingChatMessage { Id = Guid.NewGuid(), ConversationId = row.Id, Position = row.MessageCount++, Role = "assistant", Text = Clip(reply, MaxText)! }, cancellationToken);
        }
        row.LastMessageAtUtc = now;
        row.BookedName ??= Clip(bookedName, 150);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<MarketingChatConversationDto>> ListAsync(int page, int pageSize, string? search, bool bookedOnly, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var query = unitOfWork.Repository<MarketingChatConversation>().Query();
        if (bookedOnly) query = query.Where(c => c.BookedName != null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(c => c.FirstMessage.ToLower().Contains(s)
                || (c.BookedName != null && c.BookedName.ToLower().Contains(s))
                || c.Messages.Any(m => m.Text.ToLower().Contains(s)));
        }
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(c => c.LastMessageAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<MarketingChatConversationDto> { Items = rows.Select(ToDto).ToList(), TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<MarketingChatDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await unitOfWork.Repository<MarketingChatConversation>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(MarketingChatConversation), id);
        var messages = await unitOfWork.Repository<MarketingChatMessage>().Query()
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.Position)
            .Select(m => new MarketingChatMessageDto { Role = m.Role, Text = m.Text, CreatedAtUtc = m.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        return new MarketingChatDetailDto { Conversation = ToDto(row), Messages = messages };
    }

    private static MarketingChatConversationDto ToDto(MarketingChatConversation c) => new()
    {
        Id = c.Id, StartedAtUtc = c.CreatedAtUtc, LastMessageAtUtc = c.LastMessageAtUtc, MessageCount = c.MessageCount,
        FirstMessage = c.FirstMessage, Source = c.Source, Campaign = c.Campaign, LandingPath = c.LandingPath, BookedName = c.BookedName,
    };

    private static string? Clip(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= max ? text : text[..max];
    }
}
