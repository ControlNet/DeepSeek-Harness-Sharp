using System.Net;

namespace Dsh.Fleet;

/// <summary>Owns only the local SSH streams used to borrow one remote TCP proxy.</summary>
public sealed class SshOutboundTunnel : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly MachineSpec _machine;
    private readonly HashSet<SshProxyStream> _connections = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<SshProxyStream> _released;
    private Task? _disposal;
    private bool _disposed;

    public SshOutboundTunnel(MachineSpec machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ProxyUri = MachineStore.ValidateOutboundTunnel(machine)
            ?? throw new InvalidOperationException("The machine has no outbound tunnel configured.");
        _machine = machine;
        _released = Release;
    }

    public Uri ProxyUri { get; }

    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Port != ProxyUri.Port || !endpoint.Host.AsSpan().Trim("[]").Equals(ProxyUri.IdnHost.AsSpan().Trim("[]"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The connection endpoint does not match the configured remote proxy.");
        cancellationToken.ThrowIfCancellationRequested();
        SshProxyStream connection;
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            connection = new SshProxyStream(_machine, endpoint, _released);
            _connections.Add(connection);
        }
        using (linked)
        {
            try
            {
                await connection.StartAsync(linked.Token).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private void Release(SshProxyStream connection)
    {
        lock (_gate)
            _connections.Remove(connection);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null)
                return new ValueTask(_disposal);
            _disposed = true;
            _disposal = DisposeConnectionsAsync([.. _connections]);
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeConnectionsAsync(SshProxyStream[] connections)
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await Task.WhenAll(connections.Select(connection => connection.DisposeAsync().AsTask())).ConfigureAwait(false);
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }
}
