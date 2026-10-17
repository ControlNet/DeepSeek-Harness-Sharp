using System.Text.Json.Serialization;
using Dsh.Notifications;

namespace Dsh.RemoteHost;

/** Optional Notifications wire contract; method and capability values are stable. */
public static class NotificationsProtocol
{
    public const string Capability = "notify.history";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodNotifyList, MethodNotifyMarkRead]);

    public const string MethodNotifyList = "host.notify.list";
    public const string MethodNotifyMarkRead = "host.notify.markRead";
    public const string NotificationNotifyChanged = "host.notify.changed";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteNotifyQuery))]
[JsonSerializable(typeof(RemoteNotifyPage))]
[JsonSerializable(typeof(RemoteNotifyMarkRead))]
[JsonSerializable(typeof(RemoteNotificationChange))]
[JsonSerializable(typeof(NotificationRecord))]
[JsonSerializable(typeof(int))]
public partial class NotificationsProtocolJsonContext : JsonSerializerContext;
