using Microsoft.Win32.SafeHandles;

namespace Dsh.Pty;

internal sealed class UnixPtySession : IDisposable
{
    private readonly int _pid;
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly object _gate = new();
    private static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReapPollInterval = TimeSpan.FromMilliseconds(50);
    private bool _stopping;
    private bool _disposed;

    public UnixPtySession(int pid, SafeFileHandle handle, FileStream stream)
    {
        _pid = pid;
        _handle = handle;
        _stream = stream;
        Completion = MonitorAsync();
    }

    public Task<int?> Completion { get; }

    public int Pid => _pid;

    public FileStream Stream => _stream;

    public static UnixPtySession Start(PtyStartInfo info)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix PTY backend is not available on Windows");

        var (master, pid) = UnixPtyNative.Spawn(info);
        var handle = new SafeFileHandle(master, ownsHandle: true);
        // 无缓冲:带缓冲的同步 FileStream 会让并发读(输出泵)与写(输入泵)互相阻塞,导致 attach 之后数据无法流动。
        var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false);
        return new UnixPtySession(pid, handle, stream);
    }

    public void Resize(int rows, int cols)
    {
        ThrowIfDisposed();
        UnixPtyNative.Resize(_handle, rows, cols);
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopping)
                return;
            _stopping = true;
        }

        UnixPtyNative.Terminate(_pid);
        var monitor = Completion;
        if (await Task.WhenAny(monitor, Task.Delay(TerminateGrace)).ConfigureAwait(false) != monitor)
        {
            UnixPtyNative.Kill(_pid);
            // 有界等待: 某些平台/状态下 reap 迟迟不到, 不能无限等(否则调用方挂死); Dispose 关掉主端后监视继续回收子进程。
            await Task.WhenAny(monitor, Task.Delay(TerminateGrace)).ConfigureAwait(false);
        }

        Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        UnixPtyNative.Kill(_pid);
        _stream.Dispose();
        _handle.Dispose();
    }

    /**
     * Polls until the child is reaped, also after Dispose.
     * On macOS a killed child whose tty output nobody reads finishes exiting only when the master closes, and the monitor is its only reaper; stopping at Dispose left a zombie.
     */
    private async Task<int?> MonitorAsync()
    {
        while (true)
        {
            if (UnixPtyNative.TryWait(_pid, out var exitCode))
            {
                return exitCode;
            }

            await Task.Delay(ReapPollInterval).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UnixPtySession));
        }
    }
}
