using Dsh.RemoteHost;

namespace Dsh.Host;

/** Remote PTY operations use the daemon-backed session owner. */
internal sealed class HarnessRemotePtyBackend(HostPtySessions sessions) : IRemotePtyBackend
{
    public Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken)
        => sessions.StartAsync(request, HostPathPolicy.Expand(request.Cwd ?? ""), cancellationToken);

    public Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken)
        => sessions.WriteAsync(ptyId, data, cancellationToken);

    public Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken)
        => sessions.ResizeAsync(ptyId, columns, rows, cancellationToken);

    public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken)
        => sessions.StopAsync(ptyId, cancellationToken);

    public Task<IAsyncEnumerable<RemotePtyOutput>> AttachPtyAsync(string ptyId, CancellationToken cancellationToken)
        => sessions.AttachAsync(ptyId, cancellationToken);
}
