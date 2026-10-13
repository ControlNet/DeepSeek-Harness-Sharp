using Dsh.Pty;

namespace Dsh.Tui;

/** 常驻 TUI 访问 daemon pty 的入口: 便于测试替换为假实现, 不依赖真实 daemon 套接字/命名管道。 */
internal interface IDaemonPtyAccess
{
    Task<IReadOnlyList<PtyDaemonSessionDto>> ListAsync();

    Task ControlSendAsync(string ptyId, string kind, int paneId, string payload);

    /** 进入 daemon pty 的窗格级订阅: 先按窗格尺寸调整会话(与 attach 同语义), 再订阅其字节流。 */
    Task<ITerminalBacking> EnterAsync(string ptyId, int rows, int columns);
}

/** 默认实现: 直连 daemon。 */
internal sealed class RealDaemonPtyAccess : IDaemonPtyAccess
{
    public Task<IReadOnlyList<PtyDaemonSessionDto>> ListAsync()
        => PtyDaemonClient.ListAsync();

    public Task ControlSendAsync(string ptyId, string kind, int paneId, string payload)
        => PtyDaemonClient.ControlSendAsync(ptyId, kind, paneId, payload);

    public async Task<ITerminalBacking> EnterAsync(string ptyId, int rows, int columns)
    {
        // 先定尺寸再订阅: 首批快照就是窗格尺寸下的画面, 免得先按旧尺寸铺屏再重排。
        await PtyDaemonClient.ResizeAsync(ptyId, rows, columns);
        var session = await PtyDaemonClient.SubscribeAsync(ptyId);
        return new RemoteTerminalBacking(session);
    }
}
