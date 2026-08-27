using iucs.readernest.application.Common.Exceptions;
using iucs.readernest.application.Dto.Communication;
using iucs.readernest.domain.Entities.Communication;
using iucs.readernest.domain.Enums;
using iucs.readernest.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.readernest.application.Services
{
    public class ChatbotService : IChatbotService
    {
        private readonly IUnitOfWork _unitOfWork;

        /// <summary>Common filler words ignored when scoring a question against an FAQ's tokens —
        /// otherwise two unrelated questions could "match" purely by sharing "the" or "class".</summary>
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "is", "are", "do", "does", "did", "i", "my", "me", "how", "what", "when",
            "where", "why", "to", "for", "of", "in", "on", "at", "can", "will", "need", "want", "please",
            "help", "about", "you", "your", "it", "this", "that", "and", "or", "with",
        };

        public ChatbotService(IUnitOfWork unitOfWork)
        {
            _unitOfWork = unitOfWork;
        }

        public async Task<IReadOnlyList<ChatFaqDto>> ListActiveFaqsAsync(CancellationToken cancellationToken = default)
        {
            var faqs = await _unitOfWork.Repository<ChatFaq>().Query()
                .Where(f => f.IsActive)
                .OrderBy(f => f.SortOrder).ThenBy(f => f.Question)
                .ToListAsync(cancellationToken);
            return faqs.Select(ToDto).ToList();
        }

        public async Task<IReadOnlyList<ChatFaqDto>> ListAllFaqsAsync(CancellationToken cancellationToken = default)
        {
            var faqs = await _unitOfWork.Repository<ChatFaq>().Query()
                .OrderBy(f => f.SortOrder).ThenBy(f => f.Question)
                .ToListAsync(cancellationToken);
            return faqs.Select(ToDto).ToList();
        }

        public async Task<ChatFaqDto> CreateFaqAsync(SaveChatFaqRequest request, CancellationToken cancellationToken = default)
        {
            var faq = new ChatFaq();
            Apply(faq, request);
            await _unitOfWork.Repository<ChatFaq>().AddAsync(faq, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return ToDto(faq);
        }

        public async Task<ChatFaqDto> UpdateFaqAsync(Guid id, SaveChatFaqRequest request, CancellationToken cancellationToken = default)
        {
            var repository = _unitOfWork.Repository<ChatFaq>();
            var faq = await repository.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException(nameof(ChatFaq), id);
            Apply(faq, request);
            repository.Update(faq);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return ToDto(faq);
        }

        public async Task DeleteFaqAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var repository = _unitOfWork.Repository<ChatFaq>();
            var faq = await repository.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException(nameof(ChatFaq), id);
            repository.Remove(faq);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<ChatMessageDto>> ListMyMessagesAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var messages = await _unitOfWork.Repository<ChatMessage>().Query()
                .Where(m => m.UserId == userId)
                .OrderBy(m => m.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            return messages.Select(ToDto).ToList();
        }

        public async Task<AskChatbotResponse> AskAsync(Guid userId, AskChatbotRequest request, CancellationToken cancellationToken = default)
        {
            var question = (request.Message ?? string.Empty).Trim();
            if (question.Length == 0)
            {
                throw new DomainValidationException("Enter a question to ask.");
            }

            var faqs = await _unitOfWork.Repository<ChatFaq>().Query()
                .Where(f => f.IsActive)
                .ToListAsync(cancellationToken);

            var match = FindBestMatch(question, faqs);

            var userMessage = new ChatMessage { UserId = userId, Sender = ChatMessageSender.User, Text = question };
            await _unitOfWork.Repository<ChatMessage>().AddAsync(userMessage, cancellationToken);

            var escalated = match is null;
            var botMessage = new ChatMessage
            {
                UserId = userId,
                Sender = ChatMessageSender.Bot,
                Text = match?.Answer
                    ?? "I don't have an answer for that yet — I've forwarded your doubt to a teacher, who'll follow up soon.",
                MatchedFaqId = match?.Id,
            };
            await _unitOfWork.Repository<ChatMessage>().AddAsync(botMessage, cancellationToken);

            if (escalated)
            {
                await _unitOfWork.Repository<ChatEscalation>().AddAsync(new ChatEscalation
                {
                    UserId = userId,
                    Question = question,
                    Status = ChatEscalationStatus.Pending,
                }, cancellationToken);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return new AskChatbotResponse
            {
                UserMessage = ToDto(userMessage),
                BotMessage = ToDto(botMessage),
                Matched = match is not null,
                Escalated = escalated,
            };
        }

        public async Task<IReadOnlyList<ChatEscalationDto>> ListEscalationsAsync(
            ChatEscalationStatus? status,
            CancellationToken cancellationToken = default)
        {
            var query = _unitOfWork.Repository<ChatEscalation>().Query()
                .Include(e => e.User)
                .Include(e => e.ResolvedByUser)
                .AsQueryable();
            if (status is not null)
            {
                query = query.Where(e => e.Status == status);
            }

            var escalations = await query
                .OrderByDescending(e => e.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            return escalations.Select(ToDto).ToList();
        }

        public async Task<ChatEscalationDto> ResolveEscalationAsync(
            Guid id,
            Guid resolvedByUserId,
            ResolveChatEscalationRequest request,
            CancellationToken cancellationToken = default)
        {
            var repository = _unitOfWork.Repository<ChatEscalation>();
            var escalation = await repository.GetByIdAsync(id, cancellationToken)
                ?? throw new NotFoundException(nameof(ChatEscalation), id);

            escalation.Status = ChatEscalationStatus.Resolved;
            escalation.ResolutionNote = string.IsNullOrWhiteSpace(request.ResolutionNote) ? null : request.ResolutionNote.Trim();
            escalation.ResolvedByUserId = resolvedByUserId;
            escalation.ResolvedAtUtc = DateTime.UtcNow;
            repository.Update(escalation);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var reloaded = await _unitOfWork.Repository<ChatEscalation>().Query()
                .Include(e => e.User)
                .Include(e => e.ResolvedByUser)
                .FirstAsync(e => e.Id == id, cancellationToken);
            return ToDto(reloaded);
        }

        public async Task<ChatbotUsageStatsDto> GetUsageStatsAsync(CancellationToken cancellationToken = default)
        {
            var messages = _unitOfWork.Repository<ChatMessage>().Query();
            var totalQuestions = await messages.CountAsync(m => m.Sender == ChatMessageSender.User, cancellationToken);
            var answeredByBot = await messages.CountAsync(m => m.Sender == ChatMessageSender.Bot && m.MatchedFaqId != null, cancellationToken);
            var activeUsers = await messages.Select(m => m.UserId).Distinct().CountAsync(cancellationToken);

            var escalations = _unitOfWork.Repository<ChatEscalation>().Query();
            var escalatedTotal = await escalations.CountAsync(cancellationToken);
            var pending = await escalations.CountAsync(e => e.Status == ChatEscalationStatus.Pending, cancellationToken);
            var topUnanswered = await escalations
                .GroupBy(e => e.Question)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key)
                .ToListAsync(cancellationToken);

            return new ChatbotUsageStatsDto
            {
                TotalQuestions = totalQuestions,
                AnsweredByBot = answeredByBot,
                EscalatedToTeacher = escalatedTotal,
                PendingEscalations = pending,
                ActiveUsers = activeUsers,
                TopUnansweredQuestions = topUnanswered,
            };
        }

        private static void Apply(ChatFaq faq, SaveChatFaqRequest request)
        {
            faq.Question = request.Question.Trim();
            faq.Answer = request.Answer.Trim();
            faq.Keywords = string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords.Trim();
            faq.Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
            faq.IsActive = request.IsActive;
            faq.SortOrder = request.SortOrder;
        }

        /// <summary>
        /// Rule-based match: scores each active FAQ by how many of its Question/Keywords tokens
        /// also appear in the asked question, case-insensitively. Deliberately no external AI
        /// dependency — good enough for a fixed set of common doubts (fees, schedule, login,
        /// homework, ...), and admins can widen coverage just by adding Keywords to an FAQ.
        /// </summary>
        private static ChatFaq? FindBestMatch(string question, IReadOnlyList<ChatFaq> faqs)
        {
            var askedTokens = Tokenize(question);
            if (askedTokens.Count == 0)
            {
                return null;
            }

            ChatFaq? best = null;
            var bestScore = 0;

            foreach (var faq in faqs)
            {
                var faqTokens = Tokenize(faq.Question);
                foreach (var token in Tokenize(faq.Keywords ?? string.Empty))
                {
                    faqTokens.Add(token);
                }

                var score = askedTokens.Count(faqTokens.Contains);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = faq;
                }
            }

            // Require at least one real keyword overlap, not just "a row happened to exist".
            return bestScore >= 1 ? best : null;
        }

        private static HashSet<string> Tokenize(string text) =>
            text
                .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '?', '!', ':', ';', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant())
                .Where(t => t.Length > 2 && !StopWords.Contains(t))
                .ToHashSet();

        private static ChatFaqDto ToDto(ChatFaq faq) => new()
        {
            Id = faq.Id,
            Question = faq.Question,
            Answer = faq.Answer,
            Keywords = faq.Keywords,
            Category = faq.Category,
            IsActive = faq.IsActive,
            SortOrder = faq.SortOrder,
            CreatedAtUtc = faq.CreatedAtUtc,
            UpdatedAtUtc = faq.UpdatedAtUtc,
        };

        private static ChatMessageDto ToDto(ChatMessage message) => new()
        {
            Id = message.Id,
            Sender = message.Sender.ToString(),
            Text = message.Text,
            MatchedFaqId = message.MatchedFaqId,
            CreatedAtUtc = message.CreatedAtUtc,
        };

        private static ChatEscalationDto ToDto(ChatEscalation escalation) => new()
        {
            Id = escalation.Id,
            UserId = escalation.UserId,
            UserName = escalation.User is null ? "Unknown" : $"{escalation.User.FirstName} {escalation.User.LastName}".Trim(),
            Status = escalation.Status.ToString(),
            Question = escalation.Question,
            ResolutionNote = escalation.ResolutionNote,
            ResolvedByUserId = escalation.ResolvedByUserId,
            ResolvedByName = escalation.ResolvedByUser is null ? null : $"{escalation.ResolvedByUser.FirstName} {escalation.ResolvedByUser.LastName}".Trim(),
            ResolvedAtUtc = escalation.ResolvedAtUtc,
            CreatedAtUtc = escalation.CreatedAtUtc,
        };
    }
}
