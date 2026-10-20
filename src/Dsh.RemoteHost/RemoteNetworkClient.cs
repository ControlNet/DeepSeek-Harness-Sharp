using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Network domain. */
public sealed class RemoteNetworkClient : IRemoteNetwork
{
    private readonly RemoteHostClient _host;

    internal RemoteNetworkClient(RemoteHostClient host) => _host = host;

    public Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken = default)
        => _host.SendAsync(NetworkProtocol.MethodOutboundGet, null, NetworkProtocolJsonContext.Default.RemoteOutboundProxyState, cancellationToken);

    public Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default)
        => _host.SendAsync(NetworkProtocol.MethodOutboundSet, Serialize(request, NetworkProtocolJsonContext.Default.RemoteOutboundProxySetRequest), NetworkProtocolJsonContext.Default.RemoteOutboundProxyState, cancellationToken);

    public Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default)
        => _host.SendAsync(NetworkProtocol.MethodOutboundProbe, Serialize(request, NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeRequest), NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeResult, cancellationToken);

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!_host.Supports(NetworkProtocol.ReloadCapability))
            throw new NotSupportedException("The target Host does not support network.reload; upgrade it to reload its own network settings.");
        await _host.SendAsync(NetworkProtocol.MethodReload, null, HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
    }
}
