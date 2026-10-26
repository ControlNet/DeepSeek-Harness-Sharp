using Dsh.Core;
using Dsh.Fleet;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/// <summary>Owns the selected chat's authenticated, typed IDE client registration.</summary>
public sealed class GuiEditorClientBinding(IEditorService? local, Func<string, EditorRequest, EditorRequest>? trustedMap = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private RemoteHostClient? _client;
    private IAsyncDisposable? _registration;
    private string? _session;
    private string? _workspace;
    private string? _machine;
    private long _generation;
    private bool _disposed;

    public async Task UpdateAsync(MachineConnection? machine, string session, string workspace)
    {
        var generation = Interlocked.Increment(ref _generation);
        if (local is null || _disposed) return;
        try
        {
            await _gate.WaitAsync(_lifetime.Token);
            try
            {
                if (generation != Volatile.Read(ref _generation) || _disposed) return;
                var machineName = machine?.Machine.Name;
                var client = machine?.State == MachineConnectionState.Connected ? (await machine.WaitConnectedAsync(_lifetime.Token)).Client : null;
                if (!ReferenceEquals(_client, client) || _workspace != workspace || _machine != machineName)
                {
                    if (_registration is not null) await _registration.DisposeAsync();
                    _registration = null;
                    _client = null;
                    _session = null;
                    _workspace = workspace;
                    _machine = machineName;
                    if (client is null || machineName is null || !client.Supports(EditorProtocol.Capability)) return;
                    _registration = await client.Editor.RegisterClientAsync(local, _lifetime.Token,
                        trustedMap is { } map ? request => map(machineName, request) : null);
                    _client = client;
                }
                if (_client is not null && _session != session)
                {
                    if (await _client.Editor.SelectForSessionAsync(session, _lifetime.Token))
                        _session = session;
                }
            }
            finally { _gate.Release(); }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or Dsh.Transport.JsonRpcException)
        {
            // A disconnect retires this authenticated peer; the next Fleet notification uses the new lease.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        try
        {
            if (_registration is not null) await _registration.DisposeAsync();
            _registration = null;
            _client = null;
        }
        finally { _gate.Release(); }
        _lifetime.Dispose();
    }
}
