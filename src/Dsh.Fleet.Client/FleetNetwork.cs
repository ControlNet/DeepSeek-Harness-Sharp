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

    private async Task<HostLease> BorrowAsync(CancellationToken cancellationToken)
    {
        await connection.WaitSettledAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var lease = connection.CurrentLease
            ?? throw new IOException($"The target Host is not connected: {connection.Detail ?? "connection unavailable"}");
        if (lease.Lifetime.IsCancellationRequested)
            throw new IOException("The target Host disconnected before the outbound proxy request.");
        if (!lease.Client.Supports(NetworkProtocol.Capability))
            throw new NotSupportedException("The target Host does not support network.outbound; upgrade the target Host to configure its outbound proxy.");
        return lease;
    }
}
