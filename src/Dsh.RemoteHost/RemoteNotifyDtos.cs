using Dsh.Notifications;

namespace Dsh.RemoteHost;

/** host.notify.list: newest first; Kinds empty or null matches every kind. */
public sealed record RemoteNotifyQuery(
    string? SessionId = null,
    bool UnreadOnly = false,
    IReadOnlyList<NotificationKind>? Kinds = null,
    int Offset = 0,
    int? Limit = null);

/** Total counts the matching notifications before paging; UnreadCount is over the whole history. */
public sealed record RemoteNotifyPage(IReadOnlyList<NotificationRecord> Items, int Total, int UnreadCount);

/** host.notify.markRead: All marks every unread notification (of SessionId when given); otherwise Ids. */
public sealed record RemoteNotifyMarkRead(IReadOnlyList<string>? Ids = null, bool All = false, string? SessionId = null);

/** host.notify.changed: notifications that became read, and the unread count afterwards. */
public sealed record RemoteNotifyChanged(IReadOnlyList<string> ReadIds, int UnreadCount);
