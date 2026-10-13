using System.Net.Sockets;
using System.Threading.Channels;

namespace Dsh.Pty;

/**
 * 客户端侧的窗格级订阅: 从一个 daemon pty 订阅字节流(首帧是当前屏的状态重建快照), 并把输入写回该 pty。
 * 与整屏 attach 不同, 它不独占会话 —— 同一 pty 可被多个订阅者同时观看, 也可以同时被别的进程 attach。
 */
public sealed class PtyRemoteSession : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _outputPump;
    private readonly Task _inputPump;
    private byte[]? _pending;
    private int _pendingOffset;
    private bool _disposed;

    internal PtyRemoteSession(Socket socket, NetworkStream stream, PtyDaemonSessionDto info)
    {
        _socket = socket;
        _stream = stream;
        Id = info.Id;
        Command = info.Command;
        Columns = info.Columns;
        Rows = info.Rows;
        _outputPump = Task.Run(PumpOutputAsync);
        _inputPump = Task.Run(PumpInputAsync);
    }

    public string Id { get; }

    public string Command { get; }

    public int Columns { get; }

    public int Rows { get; }

    /** 会话输出(首帧为快照); 通道结束即表示该视图不再有输出(会话退出/连接断开)。 */
    public ChannelReader<byte[]> Output => _output.Reader;

    /** 输出泵结束即视图结束。 */
    public Task Completion => _outputPump;

    public bool Exited => _outputPump.IsCompleted;

    public void Send(ReadOnlyMemory<byte> data)
    {
        if (data.Length > 0 && !_disposed)
            _input.Writer.TryWrite(data.ToArray());
    }

    /** 把一段服务端重绘字节插入输出流(窗格 resize 后重建画面), 与实时输出共享同一读取顺序。 */
    public void Inject(ReadOnlyMemory<byte> data)
    {
        if (data.Length > 0 && !_disposed)
            _output.Writer.TryWrite(data.ToArray());
    }

    /** 与 PtySession.ReadAsync 同形: 读取输出缓冲(跨调用保留半截块), 供终端窗格的读取泵直接消费。 */
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (await _output.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_pending is null && _output.Reader.TryRead(out var chunk))
            {
                _pending = chunk;
                _pendingOffset = 0;
            }

            if (_pending is { } current)
            {
                var take = Math.Min(current.Length - _pendingOffset, buffer.Length);
                current.AsSpan(_pendingOffset, take).CopyTo(buffer.Span);
                _pendingOffset += take;
                if (_pendingOffset >= current.Length)
                    _pending = null;
                return take;
            }
        }

        return 0;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Send(data);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _input.Writer.TryComplete();
        // 先断开连接读, 保证阻塞中的输出泵一定退出(取消令牌对已建立的 loopback 读不可靠)。
        try
        {
            _stream.Dispose();
            _socket.Dispose();
        }
        catch (Exception)
        {
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _outputPump.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        try
        {
            await _inputPump.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }

    private async Task PumpOutputAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read == 0)
                    return;
                _output.Writer.TryWrite(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (Exception)
        {
            // 连接断开/取消: 结束视图
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    private async Task PumpInputAsync()
    {
        try
        {
            await foreach (var chunk in _input.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await _stream.WriteAsync(chunk, _cts.Token).ConfigureAwait(false);
                await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 写入失败(会话退出/连接断开): 由输出泵统一收尾
        }
    }
}
