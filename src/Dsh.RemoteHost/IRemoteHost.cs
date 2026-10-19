namespace Dsh.RemoteHost;

/**
 * 远端工作区能力边界: 本地显示、远端执行。
 * 核心会话、事件流、消息、工具、审批和登记表由本接口提供；可选能力通过领域接口提供。
 * 本地实现直连进程内服务, 远程实现经 Dsh.Transport 代理(RemoteHostClient); 两版 GUI 都通过这些成员使用。
 */
public interface IRemoteHost
{
    IRemoteAgent Agent { get; }
    IRemotePty Pty { get; }
    IRemoteFiles Files { get; }
    IRemoteGit Git { get; }
    IRemoteNotifications Notifications { get; }
    IRemoteSessionEditing SessionEditing { get; }
    IRemotePlugins Plugins { get; }
    IRemoteModels Models { get; }
    IRemoteNetwork Network { get; }

    Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection = null, RemoteProviderSpec? provider = null, CancellationToken cancellationToken = default);

    Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, RemoteModelSelection? selection = null, RemoteProviderSpec? provider = null, CancellationToken cancellationToken = default);

    Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken = default);

    Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default);

    Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default);

    /** 每次待决变化包含完整投影和可选首答审计，连接建立时先发送当前投影。 */
    IAsyncEnumerable<RemotePendingChange> PendingChangesAsync(CancellationToken cancellationToken = default);

    /** 返回 false 表示该请求已被别的客户端先回答或已取消, 本次回答无效。 */
    Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default, bool rememberForSession = false);

    /** 返回 false 表示该请求已被别的客户端先回答或已取消。 */
    Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default);

    Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default);

    /** 统一会话登记表的当前全量。 */
    Task<IReadOnlyList<RemoteHostSession>> ListHostSessionsAsync(CancellationToken cancellationToken = default);

    /** Starts with the complete registry snapshot, then per-session deltas. */
    IAsyncEnumerable<RemoteSessionChange> HostSessionChangesAsync(CancellationToken cancellationToken = default);

}
