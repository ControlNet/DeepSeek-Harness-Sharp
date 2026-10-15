using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/**
 * 终端会话的屏幕模型: 后台读循环把连接的输出喂给 VtScreen, 写循环按顺序把输入写回连接。
 * 与控件分离, 视图销毁重建(如 Dashboard 切换)时屏幕状态不丢, 多个视图也可以同时显示同一会话。
 */
public sealed class TerminalSession : IAsyncDisposable
{
    private const int ReadBufferSize = 64 * 1024;
    private const string ExitedNotice = "\r\n\u001b[0;2m[进程已退出]\u001b[0m";

    private readonly ITerminalConnection _connection;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private int _started;

    public TerminalSession(ITerminalConnection connection, TerminalSize size)
    {
        _connection = connection;
        Screen = new VtScreen(size.Columns, size.Rows)
        {
            QueryForeground = TerminalPalette.ToCellColor(TerminalPalette.DefaultForeground),
            QueryBackground = TerminalPalette.ToCellColor(TerminalPalette.DefaultBackground),
        };
        Screen.Reply += reply => Send(Encoding.UTF8.GetBytes(reply));
    }

    /** 访问 Screen 必须持有此锁: 读循环在后台线程上写屏, 渲染与输入在 UI 线程上读屏。 */
    public Lock Gate { get; } = new();

    public VtScreen Screen { get; }

    public bool HasExited { get; private set; }

    /** 屏幕内容、尺寸或退出状态变化; 在后台线程上触发, 订阅方自行切回 UI 线程并合并帧。 */
    public event Action? Changed;

    public event Action? Exited;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    /** 排队写给子进程; 写循环保证与调用顺序一致。 */
    public void Send(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 0)
            _outgoing.Writer.TryWrite(bytes.ToArray());
    }

    /** 调整屏幕尺寸并通知连接; 尺寸未变时什么也不做。 */
    public void Resize(TerminalSize size)
    {
        lock (Gate)
        {
            if (Screen.Width == size.Columns && Screen.Height == size.Rows)
                return;
            Screen.Resize(size.Columns, size.Rows);
        }
        _ = ResizeConnectionAsync(size);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        await _cts.CancelAsync();
        _cts.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[ReadBufferSize];
        try
        {
            int read;
            while ((read = await _connection.ReadAsync(buffer, _cts.Token)) > 0)
            {
                lock (Gate)
                    Screen.Feed(buffer.AsSpan(0, read));
                Changed?.Invoke();
            }
            MarkExited();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            HasExited = true;
        }
    }

    private void MarkExited()
    {
        lock (Gate)
            Screen.Feed(ExitedNotice);
        HasExited = true;
        _outgoing.Writer.TryComplete();
        Exited?.Invoke();
        Changed?.Invoke();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var chunk in _outgoing.Reader.ReadAllAsync(_cts.Token))
                await _connection.WriteAsync(chunk, _cts.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 连接已断或会话已释放: 剩余输入没有接收方, 直接丢弃。
        }
    }

    private async Task ResizeConnectionAsync(TerminalSize size)
    {
        try
        {
            await _connection.ResizeAsync(size, _cts.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or Win32Exception)
        {
            // 子进程已退出时 resize 失败无害: 屏幕模型已按新尺寸调整, 没有对端需要通知。
        }
    }
}
