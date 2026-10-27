namespace iucs.meettomanage.application.Common.Interfaces
{
    /// <summary>
    /// Delivers a push notification to every app installation a user has registered. Credentials come from the
    /// tenant's own "fcm" integration record, so each client controls its Firebase project. Never throws: push is a
    /// best-effort companion to the persisted in-app notification.
    /// </summary>
    public interface IPushSender
    {
        Task SendToUserAsync(Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data = null, CancellationToken cancellationToken = default);
    }
}
