using iucs.meettomanage.application.Dto.Mito;

namespace iucs.meettomanage.application.Common.Interfaces;

public interface IMitoAssistant
{
    Task<MitoChatResponse> ReplyAsync(
        MitoStudentContext student,
        IReadOnlyList<MitoChatTurn> conversation,
        string mode,
        CancellationToken cancellationToken = default);
}
