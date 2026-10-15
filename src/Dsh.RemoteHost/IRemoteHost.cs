using Dsh.GitInspect;
using Dsh.Notifications;

namespace Dsh.RemoteHost;

/**
 * 远端工作区能力边界: 本地显示、远端执行。
 * 一次性覆盖 会话 / 事件流 / 消息 / 工具 / 审批 / 文件 / 终端(PTY), 不留后续再补的尾巴。
 * 本地实现直连进程内服务, 远程实现经 Dsh.Transport 代理(RemoteHostClient); 两版 GUI 都通过这些成员使用。
 */
public interface IRemoteHost
{
    Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection = null, RemoteProviderSpec? provider = null, CancellationToken cancellationToken = default);

    Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, RemoteModelSelection? selection = null, RemoteProviderSpec? provider = null, CancellationToken cancellationToken = default);

    IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken = default);

    /** 停止某会话的事件推送(重新订阅前调用, 避免旧泵与新泵同时推送导致重复)。 */
    Task UnsubscribeAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default);

    Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default);

    /**
     * 审批/问答是「远端发起, 本地决策」: 消费请求流, 用 RequestId 经 RespondApprovalAsync 回填。
     * 请求广播给连到同一 host 的所有客户端, 先答者生效; 连接建立时 host 先补发全部未决请求。
     */
    IAsyncEnumerable<RemoteApprovalRequest> ApprovalsAsync(CancellationToken cancellationToken = default);

    /** 返回 false 表示该请求已被别的客户端先回答或已取消, 本次回答无效。 */
    Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default);

    /** 问答请求流(远端发起); 本地决定后经 RespondQuestionAsync 回填。 */
    IAsyncEnumerable<RemoteQuestionRequest> QuestionsAsync(CancellationToken cancellationToken = default);

    /** 返回 false 表示该请求已被别的客户端先回答或已取消。 */
    Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default);

    /** 未决请求的结局推送(谁回答了、或被取消), 客户端据此关闭对应的弹窗。 */
    IAsyncEnumerable<RemotePendingResolved> ResolutionsAsync(CancellationToken cancellationToken = default);

    Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default);

    /** 统一会话登记表的当前全量。 */
    Task<IReadOnlyList<RemoteHostSession>> ListHostSessionsAsync(CancellationToken cancellationToken = default);

    /** 登记表的变化推送: 每条是变化后那个会话的完整记录。 */
    IAsyncEnumerable<RemoteHostSession> HostSessionChangesAsync(CancellationToken cancellationToken = default);

    Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default);

    /** 列出远端某目录的子项(供“选择远端目录”浏览); Path 支持 `~` 前缀。 */
    Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken = default);

    /** attach 后先收到整屏快照, 再收到实时输出; 多个客户端可同时 attach 同一个 PTY, 断开不影响 PTY。 */
    Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken = default);

    /** 调整远端 PTY 的尺寸; 不支持 resize 的旧宿主上什么也不做。 */
    Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken = default);

    /** 终止远端某个 PTY。 */
    Task StopPtyAsync(string ptyId, CancellationToken cancellationToken = default);

    /** Agent kinds this host can start (needs agent.sessions). */
    Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken = default);

    /** Starts any kind of session (harness, terminal-mode or ACP-mode agent) and returns its registry record. */
    Task<RemoteHostSession> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken = default);

    /** One user turn for an ACP or harness session; events arrive through SubscribeAsync. */
    Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken = default);

    Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken = default);

    Task StopAgentAsync(string sessionId, CancellationToken cancellationToken = default);

    /** Restarts an exited agent session; terminal-mode agents come back under a new session id. */
    Task<RemoteHostSession> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken = default);

    /** host.git.*: ProjectPath is any directory inside the repository (needs git.inspect). */
    Task<GitStatus> GitStatusAsync(string projectPath, CancellationToken cancellationToken = default);

    Task<GitDiff> GitDiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitCommitSummary>> GitLogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string projectPath, CancellationToken cancellationToken = default);

    Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken = default);

    Task RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken = default);

    /** host.fs.* (needs fs.transfer); a missing path stats as Exists = false. */
    Task<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task RenameAsync(string from, string to, bool overwrite = false, CancellationToken cancellationToken = default);

    Task DeleteAsync(string path, bool recursive = false, CancellationToken cancellationToken = default);

    Task<RemoteUploadRef> BeginUploadAsync(string path, bool overwrite = true, CancellationToken cancellationToken = default);

    /** Returns the number of bytes the host has received so far. */
    Task<long> UploadChunkAsync(string uploadId, long offset, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    Task<RemoteFileStat> CommitUploadAsync(string uploadId, long? size = null, CancellationToken cancellationToken = default);

    Task AbortUploadAsync(string uploadId, CancellationToken cancellationToken = default);

    Task<RemoteFileChunk> DownloadChunkAsync(string path, long offset, int? length = null, CancellationToken cancellationToken = default);

    /** Streams Source to Path chunk by chunk and replaces Path atomically at the end; an error aborts the upload. */
    Task<RemoteFileStat> UploadFileAsync(string path, Stream source, bool overwrite = true, CancellationToken cancellationToken = default);

    /** Copies Path into Destination chunk by chunk; returns the number of bytes copied. */
    Task<long> DownloadFileAsync(string path, Stream destination, CancellationToken cancellationToken = default);

    /** host.notify.* (needs notify.history). */
    Task<RemoteNotifyPage> ListNotificationsAsync(RemoteNotifyQuery? query = null, CancellationToken cancellationToken = default);

    /** Returns how many notifications became read. */
    Task<int> MarkNotificationsReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken = default);

    /** host.notify.added pushes. */
    IAsyncEnumerable<NotificationRecord> NotificationsAddedAsync(CancellationToken cancellationToken = default);

    /** host.notify.changed pushes (read marks made by any client). */
    IAsyncEnumerable<RemoteNotifyChanged> NotificationChangesAsync(CancellationToken cancellationToken = default);
}
