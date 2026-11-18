namespace iucs.meettomanage.application.Common;

/// <summary>
/// Names the feature an AI call belongs to (mito, chatbot, copilot, ...), so the platform's token meter can show
/// what each academy's AI allowance was spent on. Metadata only: it never decides which key or tenant is used.
/// </summary>
public static class AiUsageScope
{
    public const string Mito = "mito";
    public const string Chatbot = "chatbot";
    public const string Copilot = "copilot";
    public const string BusinessBrief = "business-brief";
    public const string SiteChat = "site-chat";
    public const string MarketingTools = "marketing-tools";
    public const string Other = "other";

    private static readonly AsyncLocal<string?> Slot = new();

    public static string Current => Slot.Value ?? Other;

    public static IDisposable Enter(string feature)
    {
        var previous = Slot.Value;
        Slot.Value = feature;
        return new Restore(() => Slot.Value = previous);
    }

    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }
}
