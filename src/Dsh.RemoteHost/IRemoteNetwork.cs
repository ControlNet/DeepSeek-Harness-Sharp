namespace Dsh.RemoteHost;

/** Optional outbound proxy configuration on the target host. */
public interface IRemoteNetwork
{
    Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken = default);
    Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default);
    Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default);
    Task ReloadAsync(CancellationToken cancellationToken = default);
}

public interface IRemoteNetworkBackend
{
    Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken);
    Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken);
    Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken);
}

/** Independently optional: reload never accepts another process's configuration snapshot. */
public interface IRemoteNetworkReloadBackend
{
    Task ReloadAsync(CancellationToken cancellationToken);
}
