using iucs.meettomanage.api.Services;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Common.Options;
using iucs.meettomanage.application.Dto.Mito;
using Microsoft.Extensions.Options;
using Xunit;

namespace iucs.meettomanage.tests;

public class HetznerMitoAssistantTests
{
    private static readonly MitoStudentContext Student = new(
        "Asha", "Grade 5", 12, 4, 91.5, ["Math with Priya at 2026-10-28 10:00Z"], ["Fractions: equivalent fractions"]);

    [Fact]
    public async Task Sends_grounded_student_context_to_hetzner_client()
    {
        var client = new FakeAiClient();
        var assistant = new HetznerMitoAssistant(client, Options.Create(new HetznerAiOptions
        {
            ApiKey = "secret", Endpoint = "https://inference.hetzner.com/api/v1", Model = "test-model",
        }));

        var result = await assistant.ReplyAsync(Student, [new MitoChatTurn { Role = "user", Text = "What is next?" }], "plan");

        Assert.Equal("You have maths next.", result.Reply);
        Assert.Equal("test-model", client.Model);
        Assert.Equal("secret", client.Key);
        Assert.Contains("Asha", client.Messages![0].Content);
        Assert.Contains("equivalent fractions", client.Messages[0].Content);
        Assert.Equal("What is next?", client.Messages[1].Content);
    }

    [Fact]
    public async Task Missing_key_does_not_call_provider()
    {
        var client = new FakeAiClient();
        var assistant = new HetznerMitoAssistant(client, Options.Create(new HetznerAiOptions()));
        await Assert.ThrowsAsync<ExternalServiceException>(() => assistant.ReplyAsync(Student, [new MitoChatTurn { Text = "Hi" }], "general"));
        Assert.Null(client.Messages);
    }

    private sealed class FakeAiClient : IAiChatClient
    {
        public string? Key { get; private set; }
        public string? Model { get; private set; }
        public IReadOnlyList<AiChatTurn>? Messages { get; private set; }

        public Task<AiChatCompletion> CompleteAsync(string endpoint, string? bearerKey, string model, IReadOnlyList<AiChatTurn> messages,
            int maxTokens, double temperature, CancellationToken cancellationToken = default)
        {
            Key = bearerKey; Model = model; Messages = messages;
            return Task.FromResult(new AiChatCompletion("You have maths next.", 10, 5));
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string endpoint, string? bearerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
