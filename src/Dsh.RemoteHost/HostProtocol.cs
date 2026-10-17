using System.Text.Json.Serialization;

namespace Dsh.RemoteHost;

/**
 * 远端工作区协议: 本地 GUI/CLI 与远端 `dsharp host --serve` 之间的握手、能力与推送方法名。
 * 帧格式由 Dsh.Transport(JsonRpcPeer) 提供; 这里只定义方法名与 DTO。
 */
public static class HostProtocol
{
    /**
     * 线协议版本: 只在帧格式/握手/既有方法的语义发生不兼容变化时递增, 不匹配时握手显式拒绝。
     * 新增的方法、推送与可选字段不改版本, 由握手响应与 host.info 里的 capabilities 声明(见 HostProtocolCatalog)。
     */
    public const int Version = 2;

    public const string PendingBroadcastCapability = "pending.broadcast";
    public const string SessionRegistryCapability = "sessions.registry";

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
    public const string MethodSessionSnapshot = "host.session.snapshot";

    public const string MethodToolsList = "host.tools.list";
    public const string MethodApprovalRespond = "host.approval.respond";
    public const string MethodQuestionRespond = "host.question.respond";


    public const string MethodPendingList = "host.pending.list";
    public const string MethodSessionsList = "host.sessions.list";


    /** 服务端 -> 客户端 推送。 */
    public const string NotificationEvent = "host.event";
    public const string NotificationPendingChanged = "host.pending.changed";
    public const string NotificationSessionsChanged = "host.sessions.changed";
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
[JsonSerializable(typeof(RemoteSessionSnapshot))]
[JsonSerializable(typeof(string))]
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
[JsonSerializable(typeof(RemoteSubscriptionRef))]
[JsonSerializable(typeof(RemoteEventFrame))]
[JsonSerializable(typeof(RemoteMessageRequest))]
[JsonSerializable(typeof(RemoteImageBlock))]
[JsonSerializable(typeof(IReadOnlyList<RemoteImageBlock>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteSessionInfo>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(RemotePendingList))]
[JsonSerializable(typeof(RemotePendingResolved))]
[JsonSerializable(typeof(RemotePendingChange))]
[JsonSerializable(typeof(RemoteHostSession))]
[JsonSerializable(typeof(RemoteSessionChange))]
[JsonSerializable(typeof(IReadOnlyList<RemoteHostSession>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteApprovalRequest>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionRequest>))]
public partial class HostProtocolJsonContext : JsonSerializerContext;
