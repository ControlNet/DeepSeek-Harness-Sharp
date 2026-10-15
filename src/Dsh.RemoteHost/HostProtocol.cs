using System.Text.Json.Serialization;
using Dsh.GitInspect;
using Dsh.Notifications;

namespace Dsh.RemoteHost;

/**
 * 远端工作区协议: 本地 GUI/CLI 与远端 `dsharp host --serve` 之间的握手、能力与推送方法名。
 * 帧格式由 Dsh.Transport(JsonRpcPeer) 提供; 这里只定义方法名与 DTO。
 */
public static class HostProtocol
{
    /**
     * 线协议版本: 只在帧格式/握手/既有方法的语义发生不兼容变化时递增, 不匹配时握手显式拒绝。
     * 新增的方法、推送与可选字段不改版本, 由握手响应与 host.info 里的 capabilities 声明(见 HostCapabilities)。
     */
    public const int Version = 1;

    public const string MethodHello = "host.hello";
    public const string MethodPing = "host.ping";
    public const string MethodInfo = "host.info";

    public const string MethodSessionList = "host.session.list";
    public const string MethodSessionCreate = "host.session.create";
    public const string MethodSessionResume = "host.session.resume";
    public const string MethodSessionSubscribe = "host.session.subscribe";
    public const string MethodSessionUnsubscribe = "host.session.unsubscribe";
    public const string MethodSessionMessage = "host.session.message";
    public const string MethodSessionInterrupt = "host.session.interrupt";

    public const string MethodToolsList = "host.tools.list";
    public const string MethodApprovalRespond = "host.approval.respond";
    public const string MethodQuestionRespond = "host.question.respond";

    public const string MethodFileRead = "host.file.read";
    public const string MethodFileWrite = "host.file.write";
    public const string MethodFileList = "host.file.list";

    public const string MethodPtyStart = "host.pty.start";
    public const string MethodPtyWrite = "host.pty.write";
    public const string MethodPtyStop = "host.pty.stop";
    public const string MethodPtyAttach = "host.pty.attach";
    public const string MethodPtyDetach = "host.pty.detach";
    public const string MethodPtyResize = "host.pty.resize";

    public const string MethodPendingList = "host.pending.list";
    public const string MethodSessionsList = "host.sessions.list";

    public const string MethodAgentCatalog = "host.agent.catalog";
    public const string MethodAgentStart = "host.agent.start";
    public const string MethodAgentPrompt = "host.agent.prompt";
    public const string MethodAgentCancel = "host.agent.cancel";
    public const string MethodAgentStop = "host.agent.stop";
    public const string MethodAgentResume = "host.agent.resume";

    public const string MethodGitStatus = "host.git.status";
    public const string MethodGitDiff = "host.git.diff";
    public const string MethodGitLog = "host.git.log";
    public const string MethodGitWorktreeList = "host.git.worktree.list";
    public const string MethodGitWorktreeAdd = "host.git.worktree.add";
    public const string MethodGitWorktreeRemove = "host.git.worktree.remove";

    public const string MethodFsStat = "host.fs.stat";
    public const string MethodFsMkdir = "host.fs.mkdir";
    public const string MethodFsRename = "host.fs.rename";
    public const string MethodFsDelete = "host.fs.delete";
    public const string MethodFsUploadBegin = "host.fs.upload.begin";
    public const string MethodFsUploadChunk = "host.fs.upload.chunk";
    public const string MethodFsUploadCommit = "host.fs.upload.commit";
    public const string MethodFsUploadAbort = "host.fs.upload.abort";
    public const string MethodFsDownload = "host.fs.download";

    public const string MethodNotifyList = "host.notify.list";
    public const string MethodNotifyMarkRead = "host.notify.markRead";

    /** 服务端 -> 客户端 推送。 */
    public const string NotificationEvent = "host.event";
    public const string NotificationApproval = "host.approval";
    public const string NotificationQuestion = "host.question";
    public const string NotificationPtyOutput = "host.pty.output";
    public const string NotificationPendingResolved = "host.pending.resolved";
    public const string NotificationSessionsChanged = "host.sessions.changed";
    public const string NotificationNotifyAdded = "host.notify.added";
    public const string NotificationNotifyChanged = "host.notify.changed";
}

/** 能力标记: 客户端据此判断对端是否支持某项新增功能, 旧宿主不发 capabilities 即视为都不支持。 */
public static class HostCapabilities
{
    /** 审批/问答广播到所有连接, 先答者生效, host.pending.resolved 推送, host.pending.list 查询, 新连接先补发未决请求。 */
    public const string PendingBroadcast = "pending.broadcast";

    /** 统一会话登记表: host.sessions.list 与 host.sessions.changed。 */
    public const string SessionRegistry = "sessions.registry";

    /** host.pty.* 由 PTY daemon 托管: 多端同时 attach、attach 首帧整屏快照、host.pty.detach、PTY 不随 host 进程或连接结束。 */
    public const string PtyDaemon = "pty.daemon";

    /** host.pty.resize。 */
    public const string PtyResize = "pty.resize";

    /** host.agent.*: external agents in terminal or ACP mode and harness sessions behind one start entry point. */
    public const string AgentSessions = "agent.sessions";

    /** host.git.*: status, diff, log and worktrees of a project repository. */
    public const string GitInspect = "git.inspect";

    /** host.fs.*: stat, mkdir, rename, delete and chunked upload/download; every host.file.* and host.fs.* path is checked against the host's allowed roots. */
    public const string FsTransfer = "fs.transfer";

    /** host.notify.*: the host's persistent notification history, read marks shared by every client. */
    public const string NotifyHistory = "notify.history";

    public static readonly IReadOnlyList<string> All =
        [PendingBroadcast, SessionRegistry, PtyDaemon, PtyResize, AgentSessions, GitInspect, FsTransfer, NotifyHistory];
}

/** 握手请求: 客户端声明的协议版本与 token; ClientName 是客户端自报的显示名(如 "gui@laptop"), 用于标注由谁回答了审批。 */
public sealed record HostHelloRequest(string? Token, int ProtocolVersion, string? ClientName = null);

/** 握手响应: 服务端版本; Mismatch 非空即拒绝(附原因); ConnectionId 是本连接在 host 内的唯一标识。 */
public sealed record HostHelloResponse(
    bool Ok,
    int ProtocolVersion,
    string HostVersion,
    string? Mismatch,
    IReadOnlyList<string>? Capabilities = null,
    string? ConnectionId = null);

/** 服务端信息。 */
public sealed record HostInfo(int ProtocolVersion, string HostVersion, string Platform, string HomeRoot, IReadOnlyList<string>? Capabilities = null);

/** 源生成的 JSON 上下文(AOT/裁剪安全)。 */
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HostHelloRequest))]
[JsonSerializable(typeof(HostHelloResponse))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(RemoteSessionInfo))]
[JsonSerializable(typeof(RemoteEventInfo))]
[JsonSerializable(typeof(RemoteApprovalRequest))]
[JsonSerializable(typeof(RemoteApprovalResponse))]
[JsonSerializable(typeof(RemoteQuestionRequest))]
[JsonSerializable(typeof(RemoteQuestionItem))]
[JsonSerializable(typeof(RemoteQuestionOption))]
[JsonSerializable(typeof(RemoteQuestionResponse))]
[JsonSerializable(typeof(RemoteQuestionAnswerItem))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionItem>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionAnswerItem>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionOption>))]
[JsonSerializable(typeof(RemoteSessionCreateRequest))]
[JsonSerializable(typeof(RemoteSessionRef))]
[JsonSerializable(typeof(RemoteModelSelection))]
[JsonSerializable(typeof(RemoteProviderSpec))]
[JsonSerializable(typeof(RemoteSubscribeRequest))]
[JsonSerializable(typeof(RemoteMessageRequest))]
[JsonSerializable(typeof(RemoteImageBlock))]
[JsonSerializable(typeof(IReadOnlyList<RemoteImageBlock>))]
[JsonSerializable(typeof(RemoteFilePath))]
[JsonSerializable(typeof(RemoteFileContent))]
[JsonSerializable(typeof(RemoteDirectoryEntry))]
[JsonSerializable(typeof(RemoteDirectoryListing))]
[JsonSerializable(typeof(RemotePtyStartRequest))]
[JsonSerializable(typeof(RemotePtyRef))]
[JsonSerializable(typeof(RemotePtyInput))]
[JsonSerializable(typeof(RemotePtyOutput))]
[JsonSerializable(typeof(IReadOnlyList<RemoteSessionInfo>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteDirectoryEntry>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(RemotePtyResize))]
[JsonSerializable(typeof(RemotePendingList))]
[JsonSerializable(typeof(RemotePendingResolved))]
[JsonSerializable(typeof(RemoteHostSession))]
[JsonSerializable(typeof(IReadOnlyList<RemoteHostSession>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteApprovalRequest>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionRequest>))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(RemoteAgentCatalogEntry))]
[JsonSerializable(typeof(IReadOnlyList<RemoteAgentCatalogEntry>))]
[JsonSerializable(typeof(RemoteAgentStartRequest))]
[JsonSerializable(typeof(RemoteAgentPrompt))]
[JsonSerializable(typeof(RemoteFileStat))]
[JsonSerializable(typeof(RemoteFsRename))]
[JsonSerializable(typeof(RemoteFsDelete))]
[JsonSerializable(typeof(RemoteUploadBegin))]
[JsonSerializable(typeof(RemoteUploadRef))]
[JsonSerializable(typeof(RemoteUploadChunk))]
[JsonSerializable(typeof(RemoteUploadCommit))]
[JsonSerializable(typeof(RemoteDownloadRequest))]
[JsonSerializable(typeof(RemoteFileChunk))]
[JsonSerializable(typeof(RemoteGitRequest))]
[JsonSerializable(typeof(RemoteGitDiffRequest))]
[JsonSerializable(typeof(RemoteGitLogRequest))]
[JsonSerializable(typeof(RemoteGitWorktreeAdd))]
[JsonSerializable(typeof(RemoteGitWorktreeRemove))]
[JsonSerializable(typeof(GitStatus))]
[JsonSerializable(typeof(GitDiff))]
[JsonSerializable(typeof(IReadOnlyList<GitCommitSummary>))]
[JsonSerializable(typeof(GitWorktree))]
[JsonSerializable(typeof(IReadOnlyList<GitWorktree>))]
[JsonSerializable(typeof(RemoteNotifyQuery))]
[JsonSerializable(typeof(RemoteNotifyPage))]
[JsonSerializable(typeof(RemoteNotifyMarkRead))]
[JsonSerializable(typeof(RemoteNotifyChanged))]
[JsonSerializable(typeof(NotificationRecord))]
public partial class HostProtocolJsonContext : JsonSerializerContext;
