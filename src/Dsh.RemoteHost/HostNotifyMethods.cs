using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** host.notify.* requests against the host's notification history. */
internal static class HostNotifyMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(HostNotificationCenter notifications, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case HostProtocol.MethodNotifyList:
            {
                var query = message.Params is { } element ? JsonSerializer.Deserialize(element, HostProtocolJsonContext.Default.RemoteNotifyQuery) : null;
                return Serialize(notifications.Query(query ?? new RemoteNotifyQuery()), HostProtocolJsonContext.Default.RemoteNotifyPage);
            }
            case HostProtocol.MethodNotifyMarkRead:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteNotifyMarkRead, "markRead requires ids or all");
                return Serialize(await notifications.MarkReadAsync(request, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.Int32);
            }
            default:
                throw MethodNotFound(name);
        }
    }
}
