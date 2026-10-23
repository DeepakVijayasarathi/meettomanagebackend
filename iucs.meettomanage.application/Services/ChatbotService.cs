using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Communication;
using iucs.meettomanage.domain.Entities.Communication;
using iucs.meettomanage.domain.Entities.Settings;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.application.Services
{
    public class ChatbotService : IChatbotService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAiChatClient _aiChatClient;
        private readonly IPinVault _secretVault;

        private const string AiEnabledKey = "ai.chat.enabled";
        private const string AiEndpointKey = "ai.chat.endpoint";
        private const string AiModelKey = "ai.chat.model";
        private const string AiSystemPromptKey = "ai.chat.systemPrompt";
        private const string AiMaxTokensKey = "ai.chat.maxTokens";
        private const string AiTemperatureKey = "ai.chat.temperature";
        private const string AiApiKeyKey = "ai.chat.apiKeyEncrypted";

        /// <summary>Common filler words ignored when scoring a question against an FAQ's tokens —
        /// otherwise two unrelated questions could "match" purely by sharing "the" or "class".</summary>
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "is", "are", "do", "does", "did", "i", "my", "me", "how", "what", "when",
            "where", "why", "to", "for", "of", "in", "on", "at", "can", "will", "need", "want", "please",
            "help", "about", "you", "your", "it", "this", "that", "and", "or", "with",
        };

        public ChatbotService(IUnitOfWork unitOfWork, IAiChatClient aiChatClient, IPinVault secretVault)
        {
            _unitOfWork = unitOfWork;
            _aiChatClient = aiChatClient;
            _secretVault = secretVault;
        }

        public async Task<AiChatConfigurationDto> GetAiConfigurationAsync(CancellationToken cancellationToken = default)
        {
            var values = await AiSettingsAsync(cancellationToken);
            var hasKey = values.TryGetValue(AiApiKeyKey, out var encrypted) && !string.IsNullOrWhiteSpace(encrypted);
            return new AiChatConfigurationDto
            {
                Enabled = Bool(values, AiEnabledKey),
                Endpoint = Val(values, AiEndpointKey),
                Model = Val(values, AiModelKey, "smollm2:135m"),
                SystemPrompt = Val(values, AiSystemPromptKey, "Answer in 2-3 short sentences."),
                MaxTokens = Int(values, AiMaxTokensKey, 120, 1, 2000),
                Temperature = Double(values, AiTemperatureKey, 0.3, 0, 2),
                HasApiKey = hasKey,
                MaskedApiKey = hasKey ? "••••••••" : string.Empty,
            };
        }

        public async Task<AiChatConfigurationDto> UpdateAiConfigurationAsync(UpdateAiChatConfigurationRequest request, CancellationToken cancellationToken = default)
        {
            ValidateAiConfiguration(request);
            var values = new Dictionary<string, string?>
            {
                [AiEnabledKey] = request.Enabled.ToString().ToLowerInvariant(), [AiEndpointKey] = request.Endpoint.Trim(),
                [AiModelKey] = request.Model.Trim(), [AiSystemPromptKey] = request.SystemPrompt.Trim(),
                [AiMaxTokensKey] = request.MaxTokens.ToString(), [AiTemperatureKey] = request.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (request.ClearApiKey) values[AiApiKeyKey] = null;
            else if (!string.IsNullOrWhiteSpace(request.ApiKey)) values[AiApiKeyKey] = _secretVault.Protect(request.ApiKey.Trim());
            await UpsertAiSettingsAsync(values, cancellationToken);
            return await GetAiConfigurationAsync(cancellationToken);
        }

        public async Task<TestAiChatResponse> TestAiAsync(TestAiChatRequest request, CancellationToken cancellationToken = default)
        {
            var answer = await AskAiAsync((request.Message ?? string.Empty).Trim(), Array.Empty<ChatMessage>(), requireEnabled: false, cancellationToken)
                ?? throw new DomainValidationException("AI is not configured. Save an endpoint and model first.");
            return new TestAiChatResponse { Answer = answer };
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

            var userMessage = new ChatMessage { UserId = userId, Sender = ChatMessageSender.User, Text = question };
            await _unitOfWork.Repository<ChatMessage>().AddAsync(userMessage, cancellationToken);

            // Smalltalk short-circuits before FAQ matching/escalation entirely — "hi" is not a
            // doubt, and routing it to a teacher (which used to happen, since no FAQ token
            // overlaps with a greeting) just spammed the escalation queue with non-questions.
            var smallTalkReply = FindSmallTalkReply(question);
            if (smallTalkReply is not null)
            {
                var greetingMessage = new ChatMessage { UserId = userId, Sender = ChatMessageSender.Bot, Text = smallTalkReply };
                await _unitOfWork.Repository<ChatMessage>().AddAsync(greetingMessage, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                return new AskChatbotResponse
                {
                    UserMessage = ToDto(userMessage),
                    BotMessage = ToDto(greetingMessage),
                    Matched = true,
                    Escalated = false,
                };
            }

            var faqs = await _unitOfWork.Repository<ChatFaq>().Query()
                .Where(f => f.IsActive)
                .ToListAsync(cancellationToken);

            var recent = await _unitOfWork.Repository<ChatMessage>().Query()
                .Where(m => m.UserId == userId && m.Id != userMessage.Id)
                .OrderByDescending(m => m.CreatedAtUtc).Take(8).OrderBy(m => m.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            var aiAnswer = await AskAiAsync(question, recent, requireEnabled: true, cancellationToken);

            var match = FindBestMatch(question, faqs);

            var escalated = aiAnswer is null && match is null;
            var botMessage = new ChatMessage
            {
                UserId = userId,
                Sender = ChatMessageSender.Bot,
                Text = aiAnswer ?? match?.Answer
                    ?? "I don't have an answer for that yet — I've forwarded your doubt to a teacher, who'll follow up soon.",
                MatchedFaqId = aiAnswer is null ? match?.Id : null,
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
                Matched = aiAnswer is not null || match is not null,
                Escalated = escalated,
            };
        }

        private async Task<string?> AskAiAsync(string question, IReadOnlyList<ChatMessage> history, bool requireEnabled, CancellationToken cancellationToken)
        {
            var values = await AiSettingsAsync(cancellationToken);
            if ((requireEnabled && !Bool(values, AiEnabledKey)) || string.IsNullOrWhiteSpace(Val(values, AiEndpointKey))) return null;
            var turns = new List<AiChatTurn> { new("system", Val(values, AiSystemPromptKey, "Answer in 2-3 short sentences.")) };
            turns.AddRange(history.Select(m => new AiChatTurn(m.Sender == ChatMessageSender.User ? "user" : "assistant", m.Text)));
            turns.Add(new AiChatTurn("user", question));
            string? key = null;
            if (values.TryGetValue(AiApiKeyKey, out var encrypted) && !string.IsNullOrWhiteSpace(encrypted)) key = _secretVault.TryReveal(encrypted);
            try
            {
                return await _aiChatClient.CompleteAsync(Val(values, AiEndpointKey), key, Val(values, AiModelKey, "smollm2:135m"), turns,
                    Int(values, AiMaxTokensKey, 120, 1, 2000), Double(values, AiTemperatureKey, 0.3, 0, 2), cancellationToken);
            }
            catch when (requireEnabled) { return null; } // Keep FAQ/escalation available if the configured provider is temporarily down.
        }

        private async Task<Dictionary<string, string?>> AiSettingsAsync(CancellationToken ct) => await _unitOfWork.Repository<AppSetting>().Query()
            .Where(s => s.Key.StartsWith("ai.chat.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        private async Task UpsertAiSettingsAsync(Dictionary<string, string?> values, CancellationToken ct)
        {
            var repo = _unitOfWork.Repository<AppSetting>();
            var existing = await repo.Query().Where(s => values.Keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, ct);
            foreach (var pair in values)
                if (existing.TryGetValue(pair.Key, out var row)) { row.Value = pair.Value; repo.Update(row); }
                else await repo.AddAsync(new AppSetting { Key = pair.Key, Value = pair.Value, Category = SettingCategory.Integrations, IsPublic = false }, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        private static void ValidateAiConfiguration(UpdateAiChatConfigurationRequest r)
        {
            if (!Uri.TryCreate(r.Endpoint?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new DomainValidationException("Enter a valid HTTP or HTTPS AI endpoint.");
            if (string.IsNullOrWhiteSpace(r.Model)) throw new DomainValidationException("Enter an AI model.");
            if (string.IsNullOrWhiteSpace(r.SystemPrompt)) throw new DomainValidationException("Enter a system prompt.");
            if (r.MaxTokens is < 1 or > 2000) throw new DomainValidationException("Max tokens must be between 1 and 2000.");
            if (r.Temperature is < 0 or > 2) throw new DomainValidationException("Temperature must be between 0 and 2.");
        }
        private static string Val(Dictionary<string, string?> v, string k, string fallback = "") => v.TryGetValue(k, out var x) && !string.IsNullOrWhiteSpace(x) ? x : fallback;
        private static bool Bool(Dictionary<string, string?> v, string k) => bool.TryParse(Val(v, k), out var x) && x;
        private static int Int(Dictionary<string, string?> v, string k, int fallback, int min, int max) => int.TryParse(Val(v, k), out var x) ? Math.Clamp(x, min, max) : fallback;
        private static double Double(Dictionary<string, string?> v, string k, double fallback, double min, double max) => double.TryParse(Val(v, k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? Math.Clamp(x, min, max) : fallback;

        public async Task<ChatMessageDto> SubmitFeedbackAsync(
            Guid userId,
            Guid messageId,
            SubmitChatFeedbackRequest request,
            CancellationToken cancellationToken = default)
        {
            var repository = _unitOfWork.Repository<ChatMessage>();
            var message = await repository.FirstOrDefaultAsync(
                m => m.Id == messageId && m.UserId == userId && m.Sender == ChatMessageSender.Bot,
                cancellationToken)
                ?? throw new NotFoundException(nameof(ChatMessage), messageId);

            message.WasHelpful = request.Helpful;
            repository.Update(message);

            if (!request.Helpful)
            {
                // A matched FAQ isn't automatically a good answer — route it to a teacher just
                // like a no-match would, instead of trusting the keyword match blindly.
                var question = string.IsNullOrWhiteSpace(request.OriginalQuestion) ? message.Text : request.OriginalQuestion.Trim();
                await _unitOfWork.Repository<ChatEscalation>().AddAsync(new ChatEscalation
                {
                    UserId = userId,
                    Question = question,
                    Status = ChatEscalationStatus.Pending,
                }, cancellationToken);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return ToDto(message);
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
            var markedUnhelpful = await messages.CountAsync(m => m.Sender == ChatMessageSender.Bot && m.WasHelpful == false, cancellationToken);
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
                MarkedUnhelpful = markedUnhelpful,
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

        /// <summary>Checked in order; the first category whose trigger the question starts with wins.</summary>
        private static readonly (string[] Triggers, string Reply)[] SmallTalkReplies =
        [
            (["hi", "hii", "hey", "hello", "helo", "hlo", "yo", "good morning", "good afternoon", "good evening"],
             "Hi! How can I help with your doubt today?"),
            (["thanks", "thank you", "thankyou", "thx", "ty"],
             "You're welcome! Let me know if you have any other doubts."),
            (["bye", "goodbye", "good bye", "see you", "cya"],
             "Bye! Come back anytime you have a doubt."),
            (["how are you", "how r u", "hows it going", "how's it going"],
             "I'm doing well, thanks for asking! What doubt can I help you with?"),
            (["help", "what can you do", "who are you"],
             "I can answer common questions about classes, fees, login, recordings, attendance and more. If I don't know something, I'll forward it to a teacher so they can help directly."),
        ];

        /// <summary>
        /// Recognizes a short greeting/smalltalk turn so it gets a friendly reply instead of
        /// being treated as an unanswerable doubt and escalated to a teacher. Deliberately
        /// conservative: only short inputs qualify, so "hi, how do I pay my fees?" still falls
        /// through to real FAQ matching rather than being swallowed by the "hi" trigger.
        /// </summary>
        private static string? FindSmallTalkReply(string question)
        {
            var normalized = question.Trim().ToLowerInvariant().TrimEnd('!', '?', '.', ',');
            if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 4)
            {
                return null;
            }

            foreach (var (triggers, reply) in SmallTalkReplies)
            {
                foreach (var trigger in triggers)
                {
                    if (normalized == trigger || normalized.StartsWith(trigger + " ", StringComparison.Ordinal))
                    {
                        return reply;
                    }
                }
            }

            return null;
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

                var score = askedTokens.Count(t => TokenMatches(t, faqTokens));
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

        /// <summary>
        /// True on an exact token match, or a "close enough" one — a typo like "schdule" for
        /// "schedule" would otherwise never overlap at all, even though a human reads it as
        /// the obvious same word. The allowed edit distance grows with word length so short
        /// words (where one edit can turn one real word into a completely different one,
        /// e.g. "fee" → "few") still require an exact match.
        /// </summary>
        private static bool TokenMatches(string askedToken, HashSet<string> faqTokens)
        {
            if (faqTokens.Contains(askedToken))
            {
                return true;
            }

            var maxDistance = MaxEditDistanceFor(askedToken.Length);
            if (maxDistance == 0)
            {
                return false;
            }

            foreach (var faqToken in faqTokens)
            {
                if (Math.Abs(faqToken.Length - askedToken.Length) <= maxDistance &&
                    LevenshteinDistance(askedToken, faqToken) <= maxDistance)
                {
                    return true;
                }
            }

            return false;
        }

        private static int MaxEditDistanceFor(int wordLength) => wordLength switch
        {
            <= 4 => 0,
            <= 7 => 1,
            _ => 2,
        };

        /// <summary>Classic DP edit distance — small inputs only (single tokens), so O(n*m) is fine.</summary>
        private static int LevenshteinDistance(string a, string b)
        {
            var distances = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) distances[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) distances[0, j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    distances[i, j] = Math.Min(
                        Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                        distances[i - 1, j - 1] + cost);
                }
            }

            return distances[a.Length, b.Length];
        }

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
            WasHelpful = message.WasHelpful,
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
