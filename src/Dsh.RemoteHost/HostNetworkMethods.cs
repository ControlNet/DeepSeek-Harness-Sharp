using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

internal static class HostNetworkMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(IRemoteNetworkBackend backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case NetworkProtocol.MethodOutboundGet:
                return Serialize(await backend.GetOutboundAsync(cancellationToken).ConfigureAwait(false), NetworkProtocolJsonContext.Default.RemoteOutboundProxyState);
            case NetworkProtocol.MethodOutboundSet:
            {
                const string error = "outbound.set requires expectedRevision and settings";
                var request = Require(message, NetworkProtocolJsonContext.Default.RemoteOutboundProxySetRequest, error);
                if (request.Settings is null || string.IsNullOrWhiteSpace(request.ExpectedRevision))
                    throw new JsonRpcException(InvalidParams, error);
                return Serialize(await backend.SetOutboundAsync(request, cancellationToken).ConfigureAwait(false), NetworkProtocolJsonContext.Default.RemoteOutboundProxyState);
            }
            case NetworkProtocol.MethodOutboundProbe:
            {
                var request = Require(message, NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeRequest, "outbound.probe requires a request");
                return Serialize(await backend.ProbeOutboundAsync(request, cancellationToken).ConfigureAwait(false), NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeResult);
            }
            default:
                throw MethodNotFound(name);
        }
    }
}
