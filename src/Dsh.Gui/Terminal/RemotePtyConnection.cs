using System.IO.Pipelines;
using Dsh.Pty;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.Terminal;

/** 远端 PTY 的终端连接: 经 host.pty RPC 起进程并 attach, 本地只用两条管道搬运字节。 */
public sealed class RemotePtyConnection : ITerminalConnection, IAsyncDisposable
{
    private readonly IRemoteHost _host;
    private readonly string _ptyId;
    private readonly Pipe _input = new();
    private readonly Pipe _output = new();
    private readonly Stream _inputStream;
    private readonly Stream _outputStream;
    private readonly CancellationTokenSource _cts = new();

    private RemotePtyConnection(IRemoteHost host, string ptyId)
    {
        _host = host;
        _ptyId = ptyId;
        _inputStream = _input.Writer.AsStream();
        _outputStream = _output.Reader.AsStream();
        _ = AttachAsync();
    }

    public static async Task<RemotePtyConnection> StartAsync(IRemoteHost host, string fileName, IReadOnlyList<string> arguments, TerminalSize size, CancellationToken cancellationToken)
    {
        var ptyId = await host.StartPtyAsync(new RemotePtyStartRequest(fileName, arguments, Columns: size.Columns, Rows: size.Rows), cancellationToken);
        return new RemotePtyConnection(host, ptyId);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _outputStream.ReadAsync(buffer, cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _inputStream.WriteAsync(data, cancellationToken);
        await _inputStream.FlushAsync(cancellationToken);
    }

    /** 远端 PTY 跟随控件的格子数; 旧宿主不支持 resize 时保持启动尺寸。 */
    public async ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken)
    {
        try
        {
            await _host.ResizePtyAsync(_ptyId, size.Columns, size.Rows, cancellationToken);
        }
        catch (JsonRpcException)
        {
            // 远端 PTY 已退出: 屏幕模型已按新尺寸调整, 没有对端需要通知。
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _input.Writer.CompleteAsync();
        try
        {
            await _host.StopPtyAsync(_ptyId, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // 连接已断开: 远端 PTY 留在 PTY daemon 里, 可从别的客户端 attach 或 stop。
        }
        _cts.Dispose();
    }

    /** attach 结束(远端退出或连接断开)时结束输出管道, 读方据此读到流结束。 */
    private async Task AttachAsync()
    {
        try
        {
            await _host.AttachPtyAsync(_ptyId, _input.Reader.AsStream(), _output.Writer.AsStream(), _cts.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException)
        {
            // 断开即结束, 由下面结束输出管道通知读方。
        }
        await _output.Writer.CompleteAsync();
    }
}
