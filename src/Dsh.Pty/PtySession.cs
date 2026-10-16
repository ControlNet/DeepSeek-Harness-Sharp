using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Dsh.Pty;

public sealed class PtySession : IDisposable
{
    private readonly PtyStartInfo _startInfo;
    private readonly object _gate = new();

    /** 常驻泵把后端输出写进这里, 消费者(attach 隧道/测试)从这里读。 */
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /** 会话自己的 VT 屏: attach 据此做完整重绘(tmux 式状态重建), 而不是原始字节回放。 */
    private readonly VtScreen _screen;

    /** 窗格级(非独占)订阅者: 每个订阅者拿到当前屏快照与后续输出的一份拷贝。 */
    private readonly List<Channel<byte[]>> _subscribers = [];

    /** 子进程退出后留给泵读完尾巴输出再结束流的时间(见 OnConPtyExited)。 */
    private const int ExitDrainMilliseconds = 150;

    private readonly CancellationTokenSource _pumpCts = new();
    private readonly PtyOutputReader _outputReader;
    private UnixPtySession? _unix;
    private ConPtySession? _conPty;
    private Process? _process;
    private Task? _completion;
    private bool _attached;
    private bool _disposed;
    private bool _subscriptionsClosed;
    private PtySessionStatus _status = PtySessionStatus.Running;
    private int? _exitCode;

    internal PtySession(PtySessionId id, PtyStartInfo startInfo)
    {
        Id = id;
        _startInfo = startInfo;
        Command = startInfo.Command;
        StartedAt = DateTimeOffset.UtcNow;
        _screen = new VtScreen(startInfo.Columns, startInfo.Rows);
        _outputReader = new PtyOutputReader(_output.Reader);
    }

    public PtySessionId Id { get; }

    public string Command { get; }

    public DateTimeOffset StartedAt { get; }

    public int? ProcessId => _unix?.Pid ?? _conPty?.Pid ?? _process?.Id;

    public PtySessionStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_gate)
                return _exitCode;
        }
    }

    public bool IsAttached
    {
        get
        {
            lock (_gate)
                return _attached;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            _conPty = ConPtySession.TryStart(_startInfo);
            if (_conPty is not null)
            {
                _completion = ObserveExitAsync(_conPty.Completion);
                PumpAsync();
                return;
            }

            _process = StartWindowsProcess(_startInfo);
            _completion = _process.WaitForExitAsync().ContinueWith(
                _ => OnProcessExited(_process.ExitCode), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            PumpAsync();
            return;
        }

        _unix = UnixPtySession.Start(_startInfo);
        _completion = ObserveExitAsync(_unix.Completion);
        PumpAsync();
    }

    /**
     * 读会话输出。数据来自常驻泵而不是后端本身: 泵让"没人 attach"的会话也被持续读走(否则子进程写满 PTY 缓冲就卡住),
     * 同时把最近输出留在回放缓冲里, 供 attach 时先铺屏。
     */
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _outputReader.ReadAsync(buffer, cancellationToken);

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => (_completion ?? throw new InvalidOperationException("PTY process has not been started")).WaitAsync(cancellationToken);

    /**
     * attach 时的**状态重建**: 用会话自己的 VT 屏做全量重绘(进备用屏 + 清屏 + 逐行定位输出 + SGR/光标状态),
     * 而不是把原始字节再放一遍 —— 这样 vim/htop 这类全屏程序接管后也是正确画面(与 tmux 行为一致)。
     */
    /** 丢掉尚在输出队列里的历史字节: 新 proxy attach 时先清空, 否则早先的 detach 标记会让它立刻退出。 */
    public void DrainPendingOutput()
    {
        while (_output.Reader.TryRead(out _))
        {
        }
    }

    public byte[] Snapshot()
    {
        lock (_gate)
        {
            var output = new StringBuilder();
            output.Append("\u001b[?1049h\u001b[2J\u001b[H");
            output.Append(_screen.CursorVisible ? "\u001b[?25h" : "\u001b[?25l");
            for (var y = 0; y < _screen.Height; y++)
            {
                output.Append('\u001b').Append('[').Append(y + 1).Append(";1H");
                AppendRow(output, _screen.Row(y));
                output.Append("\u001b[K");
            }

            output.Append('\u001b').Append('[').Append(_screen.CursorY + 1).Append(';').Append(_screen.CursorX + 1).Append('H');
            return Encoding.UTF8.GetBytes(output.ToString());
        }
    }

    public string SnapshotText()
    {
        lock (_gate)
        {
            var output = new StringBuilder();
            for (var y = 0; y < _screen.Height; y++)
            {
                var row = _screen.Row(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var character = row[x].Character;
                    if (character != '\0')
                        output.Append(character);
                }
                output.AppendLine();
            }
            while (output.Length > 0 && char.IsWhiteSpace(output[output.Length - 1]))
                output.Length--;
            return output.ToString();
        }
    }

    private static void AppendRow(StringBuilder output, ReadOnlySpan<Cell> row)
    {
        var last = row.Length - 1;
        while (last >= 0 && row[last] == default)
            last--;

        var current = default(Cell);
        for (var x = 0; x <= last; x++)
        {
            var cell = row[x];
            if (cell.Character == '\0')
                continue;   // 宽字符的续格: 由前一个宽字符占位

            if (cell.Foreground != current.Foreground || cell.Background != current.Background || cell.Style != current.Style)
            {
                AppendSgr(output, cell);
                current = cell;
            }

            output.Append(cell.Character);
        }

        output.Append("\u001b[0m");
    }

    private static void AppendSgr(StringBuilder output, Cell cell)
    {
        output.Append("\u001b[0");
        if ((cell.Style & CellStyle.Bold) != 0)
            output.Append(";1");
        if ((cell.Style & CellStyle.Dim) != 0)
            output.Append(";2");
        if ((cell.Style & CellStyle.Reverse) != 0)
            output.Append(";7");
        if (!cell.Foreground.IsDefault)
            output.Append(';').Append(ForegroundSegment(cell.Foreground));
        if (!cell.Background.IsDefault)
            output.Append(';').Append(BackgroundSegment(cell.Background));
        output.Append('m');
    }

    private static string ForegroundSegment(CellColor color)
        => color.IsRgb ? $"38;2;{color.R};{color.G};{color.B}" : ForegroundCode(color.IsPalette ? color.PaletteColor : AnsiColor.Default).ToString();

    private static string BackgroundSegment(CellColor color)
        => color.IsRgb ? $"48;2;{color.R};{color.G};{color.B}" : (ForegroundCode(color.IsPalette ? color.PaletteColor : AnsiColor.Default) + 10).ToString();

    /** 调色板 1..8 → 30..37, 9..16 → 90..97(背景再 +10)。 */
    private static int ForegroundCode(AnsiColor color)
        => color <= AnsiColor.White ? 29 + (int)color : 81 + (int)color;

    private void PumpAsync() => Task.Factory.StartNew(PumpLoop, TaskCreationOptions.LongRunning);

    /**
     * 泵用**专用线程 + 同步读**: Unix 后端是无缓冲同步 FileStream, 用 ReadAsync 会在阻塞期间占住它的
     * async 信号量, 让同一会话的并发写入(attach 输入/测试写入)一起卡住 —— 这正是 WSL 上 daemon 往返用例超时的原因。
     */
    private void PumpLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!_pumpCts.IsCancellationRequested)
            {
                var read = ReadBackend(buffer);
                if (read <= 0)
                    break;
                var chunk = buffer.AsSpan(0, read).ToArray();
                lock (_gate)
                {
                    _screen.Feed(chunk);
                    foreach (var subscriber in _subscribers)
                        subscriber.Writer.TryWrite(chunk);
                }

                _output.Writer.TryWrite(chunk);
            }
        }
        catch (Exception)
        {
            // 后端读失败(进程退出/句柄关闭): 结束泵, 消费者读到 0 即认为流已结束。
        }
        finally
        {
            CompleteSubscribers();
            _output.Writer.TryComplete();
        }
    }

    private int ReadBackend(Span<byte> buffer)
    {
        var unix = _unix;
        if (unix is not null)
            return unix.Stream.Read(buffer);

        var conPty = _conPty;
        if (conPty is not null)
            return conPty.Stream.Read(buffer);

        var process = _process;
        if (process is not null)
            return process.StandardOutput.BaseStream.Read(buffer);

        throw new ObjectDisposedException(nameof(PtySession));
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var unix = _unix;
        if (unix is not null)
        {
            await unix.Stream.WriteAsync(data, cancellationToken);
            return;
        }

        var conPty = _conPty;
        if (conPty is not null)
        {
            await conPty.Input.WriteAsync(data, cancellationToken);
            return;
        }

        var process = _process;
        if (process is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(data, cancellationToken);
            return;
        }

        throw new ObjectDisposedException(nameof(PtySession));
    }

    /**
     * 把鼠标事件注入本会话子进程的控制台输入缓冲(终端形态下代理转发过来的)。
     * 不能写 pty: ConPTY 不把鼠标序列翻译成记录, 记录读取型的大厅 TUI 只会看到字面字符。
     */
    public void InjectMouse(PtyMouseEvent mouse)
    {
        if (OperatingSystem.IsWindows() && ProcessId is { } processId)
            WindowsConsoleMouseInjector.Inject(processId, mouse);
    }

    public void Resize(int rows, int columns)
    {
        lock (_gate)
            _screen.Resize(columns, rows);
        var unix = _unix;
        if (unix is not null)
        {
            unix.Resize(rows, columns);
            return;
        }

        var conPty = _conPty;
        if (conPty is not null)
        {
            conPty.Resize(rows, columns);
            return;
        }

        if (_process is not null)
            return;

        throw new ObjectDisposedException(nameof(PtySession));
    }

    public void Attach()
    {
        lock (_gate)
            _attached = true;
    }

    public void Detach()
    {
        lock (_gate)
            _attached = false;
    }

    /**
     * 窗格级(非独占)订阅: 用于把本会话显示在另一个进程/窗格里, 与独占 attach 不同 —— 不改变 attached 状态、
     * 不排空历史输出、可多方同时订阅。首帧是当前屏的状态重建快照(与 attach 同一套字节), 之后是原始输出。
     */
    public PtySessionSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate)
        {
            channel.Writer.TryWrite(Snapshot());
            // Pump EOF and the exit watcher race: once the pump has closed the subscriptions, a late subscriber gets the snapshot on an already-completed stream.
            if (_subscriptionsClosed || _status == PtySessionStatus.Exited)
                channel.Writer.TryComplete();
            else
                _subscribers.Add(channel);
        }

        return new PtySessionSubscription(this, channel);
    }

    internal void Unsubscribe(Channel<byte[]> channel)
    {
        lock (_gate)
            _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    /** 结束所有窗格级订阅: 泵退出与 ConPTY 退出两条路径都要走这里, 否则订阅者会永远等不到流结束。 */
    private void CompleteSubscribers()
    {
        lock (_gate)
        {
            _subscriptionsClosed = true;
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    public async Task StopAsync()
    {
        // 只取消、不 await 泵: Unix 后端用的是无缓冲同步 FileStream(见 UnixPtySession), 阻塞中的 ReadAsync
        // 对取消令牌不敏感, await 会死等; 先停后端(关句柄/杀进程)自然会把泵打断。
        await _pumpCts.CancelAsync().ConfigureAwait(false);
        _output.Writer.TryComplete();
        var unix = _unix;
        if (unix is not null)
        {
            await unix.StopAsync();
            lock (_gate)
            {
                _status = PtySessionStatus.Exited;
                _exitCode ??= 0;
            }

            return;
        }

        var conPty = _conPty;
        if (conPty is not null)
        {
            await conPty.StopAsync();
            lock (_gate)
            {
                _status = PtySessionStatus.Exited;
                _exitCode ??= 0;
            }

            return;
        }

        var process = _process;
        if (process is not null)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }

            lock (_gate)
            {
                _status = PtySessionStatus.Exited;
                _exitCode = process.ExitCode;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // 只取消不 Dispose: PtyHost.Dispose 之后还会调 StopAsync, 释放过的 CTS 会让 CancelAsync 抛 ObjectDisposedException。
        _pumpCts.Cancel();
        _output.Writer.TryComplete();
        CompleteSubscribers();

        if (_unix is not null)
        {
            _unix.Dispose();
        }

        if (_conPty is not null)
        {
            _conPty.Dispose();
        }

        _process?.Dispose();
    }

    internal PtySessionInfo ToInfo()
    {
        lock (_gate)
            return new PtySessionInfo(Id, Command, StartedAt, ProcessId, _status, _exitCode, _attached, _screen.Width, _screen.Height, _startInfo.WantsMouse, _startInfo.Home);
    }

    private async Task ObserveExitAsync(Task<int?> completion)
    {
        var exitCode = await completion.ConfigureAwait(false);
        if (_conPty is not null)
            OnConPtyExited(exitCode);
        else
            OnProcessExited(exitCode);
    }

    private void OnProcessExited(int? exitCode)
    {
        lock (_gate)
        {
            _status = PtySessionStatus.Exited;
            _exitCode = exitCode;
        }
    }

    private void OnConPtyExited(int? exitCode)
    {
        lock (_gate)
        {
            _status = PtySessionStatus.Exited;
            _exitCode = exitCode;
        }

        // ConPTY 的输出管道在子进程退出后不 EOF(pseudoconsole 还握着写端), 泵会一直在读上阻塞:
        // 输出流不结束 → attach 隧道永远收不到流结束 → 代理退不出会话(终端的备用屏/原始模式也还不回去),
        // GPU 形态则一直等 Tunnel 结束而关不掉窗口。稍等片刻让泵读完尾巴, 再结束输出流(窗格订阅同理)。
        _ = Task.Delay(ExitDrainMilliseconds)
            .ContinueWith(
                _ =>
                {
                    CompleteSubscribers();
                    _output.Writer.TryComplete();
                },
                TaskScheduler.Default);
    }

    private static Process StartWindowsProcess(PtyStartInfo info)
    {
        var startInfo = new ProcessStartInfo(info.FileName)
        {
            WorkingDirectory = info.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in info.Arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.AutoFlush = true;
        return process;
    }
}
