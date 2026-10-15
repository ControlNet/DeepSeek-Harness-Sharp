using System.IO.Pipelines;
using Dsh.Pty;
using Dsh.RemoteHost;

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

    public static async Task<RemotePtyConnection> StartAsync(IRemoteHost host, string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var ptyId = await host.StartPtyAsync(fileName, arguments, cancellationToken);
        return new RemotePtyConnection(host, ptyId);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _outputStream.ReadAsync(buffer, cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _inputStream.WriteAsync(data, cancellationToken);
        await _inputStream.FlushAsync(cancellationToken);
    }

    /** host.pty 协议还没有 resize 方法: 远端 PTY 保持启动时的尺寸, 本地屏幕模型照常按控件尺寸调整。 */
    public ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken) => ValueTask.CompletedTask;

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
            // 连接已断开时远端 PTY 随 host 进程一并结束, 无需再停。
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
