namespace Dsh.RemoteHost;

/**
 * 远端宿主的能力后端: 远端进程内的真实实现(harness)或测试替身都实现它, 与传输完全分离。
 * 服务端把它暴露成 RPC; 客户端经 RemoteHostProxy 变成 IRemoteHost。
 */
public interface IRemoteHostBackend
{
    /** 未决的审批与问答: 后端经它发起请求并等待回答, 服务端把它的推送转给每个连接、把回答交给它裁决先后。 */
    HostPendingDecisions Pending { get; }

    /** 本 host 创建或恢复的会话登记表(host.sessions.*)。 */
    HostSessionRegistry Sessions { get; }

    /** host.file.*, host.fs.* and the path boundary of host.git.*. */
    HostFileSystem Files { get; }

    /** The machine's notification history (host.notify.*). */
    HostNotificationCenter Notifications { get; }

    Task<HostInfo> InfoAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken);

    Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken);

    Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken);

    Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken);

    Task InterruptAsync(string sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken);

    /** Agent kinds that can be started on this machine, with the probed executables and versions. */
    Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken);

    /** Starts a harness, terminal-mode or ACP-mode session and returns its registry record. */
    Task<RemoteHostSession> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken);

    /** Sends one user turn to an ACP or harness session; returns once the turn has been handed over, not when it ends. */
    Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken);

    /** Cancels the running turn of an ACP or harness session. */
    Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken);

    /** Ends the session's process (or harness agent) and marks it exited. */
    Task StopAgentAsync(string sessionId, CancellationToken cancellationToken);

    /** Restarts an exited session with its agent's resume arguments; a terminal-mode agent comes back as a new session. */
    Task<RemoteHostSession> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken);

    Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken);

    Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken);

    Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken);

    Task StopPtyAsync(string ptyId, CancellationToken cancellationToken);

    /**
     * 订阅会话事件: 返回前已挂好实时监听(服务端据此在应答 subscribe 之前完成订阅, 应答后追加的事件不会漏);
     * 返回的流先补发 [fromSeq, ...) 历史, 再实时推送。
     */
    Task<IAsyncEnumerable<RemoteEventInfo>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken);

    /** 为一个连接 attach PTY: 不存在或已退出时抛出; 返回的流首帧是整屏快照, 以 Eof 结束。 */
    Task<IAsyncEnumerable<RemotePtyOutput>> AttachPtyAsync(string ptyId, CancellationToken cancellationToken);
}
