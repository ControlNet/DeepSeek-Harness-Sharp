using Dsh.RemoteHost;

namespace Dsh.Fleet;

/** Outbound settings belong to the target Host; Fleet only borrows its existing connection. */
public sealed class FleetNetwork(MachineConnection connection) : IRemoteNetwork
{
    public async Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken = default)
    {
        var lease = await BorrowAsync(cancellationToken).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        try
        {
            return await lease.Client.Network.GetOutboundAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lease.Lifetime.IsCancellationRequested)
        {
            throw new IOException("The target Host disconnected during the outbound proxy request.");
        }
    }

    public async Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default)
    {
        var lease = await BorrowAsync(cancellationToken).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        try
        {
            return await lease.Client.Network.SetOutboundAsync(request, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lease.Lifetime.IsCancellationRequested)
        {
            throw new IOException("The target Host disconnected during the outbound proxy request.");
        }
    }

    public async Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default)
    {
        var lease = await BorrowAsync(cancellationToken).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        try
        {
            return await lease.Client.Network.ProbeOutboundAsync(request, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lease.Lifetime.IsCancellationRequested)
        {
            throw new IOException("The target Host disconnected during the outbound proxy request.");
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var lease = await BorrowAsync(cancellationToken, NetworkProtocol.ReloadCapability).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        try
        {
            await lease.Client.Network.ReloadAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lease.Lifetime.IsCancellationRequested)
        {
            throw new IOException("The target Host disconnected during the network reload request.");
        }
    }

    private async Task<HostLease> BorrowAsync(CancellationToken cancellationToken, string capability = NetworkProtocol.Capability)
    {
        await connection.WaitSettledAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var lease = connection.CurrentLease
            ?? throw new IOException($"The target Host is not connected: {connection.Detail ?? "connection unavailable"}");
        if (lease.Lifetime.IsCancellationRequested)
            throw new IOException("The target Host disconnected before the outbound proxy request.");
        if (!lease.Client.Supports(capability))
            throw new NotSupportedException($"The target Host does not support {capability}; upgrade the target Host to use this network operation.");
        return lease;
    }
}
