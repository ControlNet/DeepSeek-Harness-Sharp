namespace Dsh.RemoteHost;

/** 会话摘要: Cwd/Status/时间供侧栏分组与排序, 标题与摘要供列表显示。 */
public sealed record RemoteSessionInfo(string Id, string Cwd, string Status, long StartedAt, string? Title, string? Summary, long UpdatedAt);

/** 会话事件流的一条: EventJson 是该 SessionEvent 的完整序列化(含 type/seq/time/data/surfaceOp), 本地据此重建会话。 */
public sealed record RemoteEventInfo(string SessionId, string EventJson, long Sequence, bool Reset = false);

/** 每个读者有独立订阅标识，历史和实时事件均由 Host 提供。 */
public sealed record RemoteSubscribeRequest(string SessionId, long FromSeq, string SubscriptionId);

public sealed record RemoteSubscriptionRef(string SubscriptionId);

public readonly record struct RemoteEventFrame(string SubscriptionId, RemoteEventInfo Event);

public sealed record RemotePtyAttachRequest(string PtyId, string SubscriptionId);

public readonly record struct RemotePtyFrame(string SubscriptionId, RemotePtyOutput Output);

public sealed record RemoteSessionSnapshot(string HeaderJson, IReadOnlyList<RemoteEventInfo> Events, string? ApprovalPolicy = null);

public sealed record RemoteCommandRequest(string SessionId, string Line);

public sealed record RemoteCommandResult(bool Known, string? Text, string? FollowupPrompt);

public sealed record RemoteCommandDescriptor(string Name, string Description);

public sealed record RemoteSessionRenameRequest(string SessionId, string Title);

public sealed record RemoteSessionPolicyRequest(string SessionId, string Policy);

public sealed record RemoteSubagentInfo(string SessionId, string? Parent, int Depth, string Mode, string? Label, bool HasChildren, bool IsLive, long CreatedAt);

public sealed record RemotePluginInfo(string Package, string Description, bool Enabled, bool ReadAllowed, bool WriteAllowed, bool NetworkAllowed);

public sealed record RemotePluginPermissionRequest(string Package, string Permission, bool Allowed);

public sealed record RemoteModelRef(string Provider, string Model);

public sealed record RemoteProviderRef(string Provider);

/** 远端发起的审批/问答: 需本地决策, 用 RequestId 回填。 */
public sealed record RemoteApprovalRequest(string RequestId, string SessionId, string Tool, string? Arguments, string? Reason);

/** 远端发起的问答: 问题项与本地 ask_user_question 同构。 */
public sealed record RemoteQuestionItem(
    string Id,
    string Question,
    string? Detail,
    string? Header,
    IReadOnlyList<RemoteQuestionOption> Options,
    bool MultiSelect,
    string? ApproveLabel);

public sealed record RemoteQuestionOption(string Label, string? Description);

public sealed record RemoteQuestionRequest(string RequestId, string SessionId, IReadOnlyList<RemoteQuestionItem> Questions);

public sealed record RemoteQuestionAnswerItem(string Id, IReadOnlyList<string> Selected, string? Custom);

public sealed record RemoteQuestionResponse(string RequestId, IReadOnlyList<RemoteQuestionAnswerItem> Answers);

/** 远端会话的模型选择: 客户端当前的 provider/model/推理强度; 缺省即远端沿用自身 settings.yaml。 */
public sealed record RemoteModelSelection(string Provider, string Model, string? ReasoningEffort = null);

/** 远端 provider 规格: 远端可能没有任何配置, 客户端带上端点信息供远端按需注册适配器。 */
public sealed record RemoteProviderSpec(string Id, string? Type = null, string? BaseUrl = null, string? ApiKey = null, string? ApiKeyEnv = null);

public sealed record RemoteSessionCreateRequest(string Cwd, RemoteModelSelection? Selection = null, RemoteProviderSpec? Provider = null);

public sealed record RemoteSessionRef(string SessionId, RemoteModelSelection? Selection = null, RemoteProviderSpec? Provider = null);

public sealed record RemoteMessageRequest(string SessionId, string Text, IReadOnlyList<RemoteImageBlock>? Images = null);

/** 远端消息里的一张图片: 字节经 base64 传输, 由远端落进附件库后作为 ImageBlock 发给会话。 */
public sealed record RemoteImageBlock(string MediaType, int Width, int Height, string? Name, string Base64);

public sealed record RemoteApprovalResponse(string RequestId, bool Allow, string? Reason, bool RememberForSession = false);

public sealed record RemoteFilePath(string Path);

/**
 * 远端文件系统中的一项: 名称 + 绝对路径 + 是否目录。
 * Size(文件字节数, 目录为 0)与 ModifiedAt(Unix 毫秒)是后加的可选字段, 旧 host 不发送时为 null。
 */
public sealed record RemoteDirectoryEntry(string Name, string Path, bool IsDirectory, long? Size = null, long? ModifiedAt = null);

/** 一次远端目录列举: 被列举目录的绝对路径 + 父目录(根为 null) + 子项。 */
public sealed record RemoteDirectoryListing(string Path, string? Parent, IReadOnlyList<RemoteDirectoryEntry> Entries);

public sealed record RemoteFileContent(string Path, string Base64);

/** 起 PTY: Cwd/Title 为空时取远端家目录与命令行; 尺寸为空时用 daemon 默认值。 */
public sealed record RemotePtyStartRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? Cwd = null,
    int? Columns = null,
    int? Rows = null,
    string? Title = null);

public sealed record RemotePtyResize(string PtyId, int Columns, int Rows);

public sealed record RemotePtyRef(string PtyId);

public sealed record RemotePtyInput(string PtyId, string Base64);

public sealed record RemotePtyOutput(string PtyId, string Base64, bool Eof);

/** 全部未决的审批与问答(host.pending.list), 也是新连接补发的内容。 */
public sealed record RemotePendingList(IReadOnlyList<RemoteApprovalRequest> Approvals, IReadOnlyList<RemoteQuestionRequest> Questions);

/** 未决请求的结局: Kind 见 PendingKinds, Resolution 见 PendingResolutions; 由 host 取消时 ResolvedBy 为 "host"、ResolvedByConnection 为空。 */
public sealed record RemotePendingResolved(
    string RequestId,
    string Kind,
    string SessionId,
    string Resolution,
    string ResolvedBy,
    string? ResolvedByConnection);

public static class PendingKinds
{
    public const string Approval = "approval";
    public const string Question = "question";
}

public static class PendingResolutions
{
    public const string Allowed = "allowed";
    public const string Rejected = "rejected";
    public const string Answered = "answered";

    /** 会话被中断、结束或 host 退出, 请求不再有人等待。 */
    public const string Cancelled = "cancelled";
}

/**
 * 统一会话模型的一条(host.sessions.list / host.sessions.changed): 只登记由本 host 创建或恢复的会话, 不扫描外部进程。
 * Kind 见 HostSessionKinds, Status 见 HostSessionStatuses; 时间为 Unix 毫秒; ExitCode 只对已退出的进程型会话有值。
 */
public sealed record RemoteHostSession(
    string SessionId,
    string Kind,
    string? AgentKind,
    string? ProjectPath,
    string? Title,
    string Status,
    long CreatedAt,
    long LastActivityAt,
    int PendingCount,
    int? ExitCode,
    bool Deleted = false);

public static class HostSessionKinds
{
    public const string Harness = "harness";
    public const string External = "external";
    public const string Acp = "acp";
    public const string Terminal = "terminal";
}

/** agentKind 取值: 会话里跑的是哪种 Agent; 外部 Agent 的取值由各自的适配器登记。 */
public static class HostAgentKinds
{
    /** 本项目自己的 Agent(harness 会话)。 */
    public const string Dsharp = "dsharp";
}

/** 只反映进程与交互状态, 不判断任务是否完成。 */
public static class HostSessionStatuses
{
    public const string Running = "Running";
    public const string WaitingForUser = "WaitingForUser";
    public const string Idle = "Idle";
    public const string Exited = "Exited";
}
