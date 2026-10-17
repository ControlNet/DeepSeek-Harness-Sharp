namespace Dsh.RemoteHost;

/** Optional notification history and change stream capabilities. */
public interface IRemoteNotifications
{
    /** host.notify.* (needs notify.history). */
    Task<RemoteNotifyPage> ListNotificationsAsync(RemoteNotifyQuery? query = null, CancellationToken cancellationToken = default);

    /** Returns how many notifications became read. */
    Task<int> MarkNotificationsReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken = default);

    /** Authoritative notification pages; Added and ReadIds describe the triggering change only. */
    IAsyncEnumerable<RemoteNotificationChange> NotificationChangesAsync(CancellationToken cancellationToken = default);
}
