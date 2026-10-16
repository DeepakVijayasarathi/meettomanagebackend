namespace iucs.meettomanage.application.Common;

/// <summary>URL metadata only. Database access always uses the explicitly bound scoped context.</summary>
public static class TenantExecution
{
    private static readonly AsyncLocal<Binding?> Slot = new();
    public sealed record Binding(Guid Id, string Code);
    public static Binding? Current => Slot.Value;
    public static IDisposable Enter(Guid id, string code)
    {
        var previous = Slot.Value;
        Slot.Value = new(id, code);
        return new Restore(() => Slot.Value = previous);
    }
    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }
    public static string Url(string baseUrl)
    {
        var root = baseUrl.TrimEnd('/');
        if (Current is not { } tenant) return root;
        var prefix = "/t/" + Uri.EscapeDataString(tenant.Code);
        return root.EndsWith(prefix, StringComparison.Ordinal) ? root : root + prefix;
    }
}
