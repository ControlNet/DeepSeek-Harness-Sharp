using System.Text;
using Dsh.Pty;

namespace Dsh.Tui;

/**
 * 终端窗格的底层会话: 本地 Dsh.Pty shell 会话(PtyHost), 或 daemon pty 的窗格级订阅(跨进程远程)。
 * ShellPane 只依赖本契约做渲染/输入路由, 两种来源共用同一套 VT 渲染与按键编码。
 */
internal interface ITerminalBacking : IDisposable
{
    string Command { get; }

    int? ProcessId { get; }

    /** 底层 pty 标识(总览归属与窗格目录用)。 */
    string PtyId { get; }

    bool Exited { get; }

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    void Resize(int rows, int columns);
}

/** 本进程 PTY 会话: 关闭窗格即结束 shell 进程(与 Splitting 开出的临时 shell 语义一致)。 */
internal sealed class LocalTerminalBacking(PtySession session) : ITerminalBacking
{
    public string Command => session.Command;

    public int? ProcessId => session.ProcessId;

    public string PtyId => session.Id.Value;

    public bool Exited => session.Status == PtySessionStatus.Exited;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => session.ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => session.WriteAsync(data, cancellationToken);

    public void Resize(int rows, int columns)
    {
        try
        {
            session.Resize(rows, columns);
        }
        catch (Exception)
        {
            // 会话已退出时忽略尺寸同步失败
        }
    }

    public void Dispose()
    {
        try
        {
            PtyHost.Default.StopAsync(session.Id.Value).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 关闭竞态(进程已退出)不阻塞 UI 拆卸
        }

        session.Dispose();
    }
}

/**
 * daemon pty 的窗格级订阅: 关闭窗格只断开订阅, 不杀远端会话(daemon 里的 pty 要继续为其他客户端服务)。
 * 远程 pty 是共享的, 尺寸由 daemon 作为权威方统管: 调整后取一次重绘快照补上本地画面(与 attach 同语义)。
 */
internal sealed class RemoteTerminalBacking(PtyRemoteSession session) : ITerminalBacking
{
    public string Command => session.Command;

    public int? ProcessId => null;

    public string PtyId => session.Id;

    public bool Exited => session.Exited;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => session.ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => session.WriteAsync(data, cancellationToken);

    public void Resize(int rows, int columns) => _ = RefreshAsync(rows, columns);

    public void Dispose()
    {
        try
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 退订失败不阻塞 UI 拆卸
        }
    }

    private async Task RefreshAsync(int rows, int columns)
    {
        try
        {
            await PtyDaemonClient.ResizeAsync(session.Id, rows, columns);
            var snapshot = await PtyDaemonClient.SnapshotAsync(session.Id);
            if (snapshot.Length > 0)
                session.Inject(Encoding.UTF8.GetBytes(snapshot));
        }
        catch (Exception)
        {
            // daemon 暂时不可用/远端会话已退出: 保留本地已有画面
        }
    }
}
