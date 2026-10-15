using System.IO.Pipelines;
using Dsh.Fleet;
using Dsh.Pty;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.Terminal;

/**
 * A PTY on a fleet machine as a terminal connection: attaches through the machine's shared connection and only moves bytes through two pipes.
 * The attachment survives reconnections (the host redraws the full screen on every attach), and several clients may attach to the same PTY.
 * Only a PTY this connection started is stopped when it is disposed; attaching to a session's PTY never ends the session.
 */
public sealed class RemotePtyConnection : ITerminalConnection, IAsyncDisposable
{
    /** How long disposing waits for the machine to stop a PTY it started. */
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly MachineConnection _machine;
    private readonly string _ptyId;
    private readonly bool _ownsPty;
    private readonly Pipe _input = new();
    private readonly Pipe _output = new();
    private readonly Stream _inputStream;
    private readonly Stream _outputStream;
    private readonly CancellationTokenSource _cts = new();

    private RemotePtyConnection(MachineConnection machine, string ptyId, bool ownsPty)
    {
        _machine = machine;
        _ptyId = ptyId;
        _ownsPty = ownsPty;
        _inputStream = _input.Writer.AsStream();
        _outputStream = _output.Reader.AsStream();
        _ = AttachAsync();
    }

    public string PtyId => _ptyId;

    /** Attaches to an existing PTY (an external agent's or a terminal session's; the session id is the PTY id). */
    public static RemotePtyConnection Attach(MachineConnection machine, string ptyId) => new(machine, ptyId, ownsPty: false);

    /** Starts a new PTY on the machine and attaches to it; disposing the connection stops it. */
    public static async Task<RemotePtyConnection> StartAsync(MachineConnection machine, RemotePtyStartRequest request, CancellationToken cancellationToken)
    {
        var lease = await machine.WaitConnectedAsync(cancellationToken);
        var ptyId = await lease.Client.StartPtyAsync(request, cancellationToken);
        return new RemotePtyConnection(machine, ptyId, ownsPty: true);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _outputStream.ReadAsync(buffer, cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _inputStream.WriteAsync(data, cancellationToken);
        await _inputStream.FlushAsync(cancellationToken);
    }

    /** The PTY follows the control's grid; while the machine is unreachable the next attach's snapshot catches up instead. */
    public async ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken)
    {
        if (_machine.State != MachineConnectionState.Connected)
            return;
        try
        {
            var lease = await _machine.WaitConnectedAsync(cancellationToken);
            await lease.Client.ResizePtyAsync(_ptyId, size.Columns, size.Rows, cancellationToken);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException)
        {
            // The PTY exited or the link dropped: the screen model already has the new size and there is no peer to tell.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _input.Writer.CompleteAsync();
        if (_ownsPty && _machine.State == MachineConnectionState.Connected)
            await StopAsync();
        _cts.Dispose();
    }

    private async Task StopAsync()
    {
        using var timeout = new CancellationTokenSource(StopTimeout);
        try
        {
            var lease = await _machine.WaitConnectedAsync(timeout.Token);
            await lease.Client.StopPtyAsync(_ptyId, timeout.Token);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or OperationCanceledException or ObjectDisposedException)
        {
            // The link is gone: the PTY stays in the machine's PTY daemon, where another client can attach to it or stop it.
        }
    }

    /** When the attachment ends (the PTY exited, it is unknown, or the connection was disposed) the output pipe ends, so the reader sees end of stream. */
    private async Task AttachAsync()
    {
        try
        {
            await _machine.AttachPtyAsync(_ptyId, _input.Reader.AsStream(), _output.Writer.AsStream(), _cts.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or JsonRpcException or ObjectDisposedException)
        {
            // Ending is reported by completing the output pipe below.
        }
        await _output.Writer.CompleteAsync();
    }
}
