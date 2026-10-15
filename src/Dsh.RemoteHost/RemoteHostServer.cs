using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

public sealed record RemoteHostServerOptions(string? Token = null);

/**
 * 在一条双向流上服务一个客户端: 处理握手/探活/信息与全部能力方法, 并把会话事件/审批/会话变化/PTY 输出推给客户端。
 * 同一个实例可同时服务多条连接: 每条连接独立订阅, 审批与会话变化广播到所有连接; 连接断开即返回(会话生命周期由宿主进程决定)。
 */
public sealed class RemoteHostServer(
    RemoteHostServerOptions options,
    IRemoteHostBackend? backend = null)
{
    /** InformationalVersion 里构建追加的提交号分隔符(见 Directory.Build.props 的 Version)。 */
    private const char CommitSeparator = '+';

    /** JSON-RPC server-defined error code: a method other than hello was called before a successful hello. */
    private const int NotAuthenticated = -32001;

    /**
     * 发布版本: 远端部署是否过期的判据与 host.info 上报的版本都用它, 取自 InformationalVersion 去掉构建追加的 "+提交号"。
     * 程序集版本(AssemblyVersion)是插件 ABI 基线, 不随发版走, 见 Directory.Build.props。
     */
    public static readonly string ReleaseVersion = ReleaseVersionOf(typeof(RemoteHostServer).Assembly);

    private static string ReleaseVersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var cut = informational.IndexOf(CommitSeparator);
        var version = cut >= 0 ? informational[..cut] : informational;
        return version.Length > 0 ? version : "0.0.0";
    }

    private readonly IRemoteHostBackend? _backend = backend;
    private long _connectionSequence;

    private string? Token => options.Token;

    private bool RequiresToken => Token is not null;

    /** 没有后端时只有握手/探活/信息, 不声明任何能力。 */
    private IReadOnlyList<string> Capabilities => _backend is null ? [] : HostCapabilities.All;

    public async Task ServeAsync(Stream duplex, CancellationToken cancellationToken = default)
    {
        await using var peer = new JsonRpcPeer(duplex);
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connection = new Connection(this, peer, $"c{Interlocked.Increment(ref _connectionSequence)}", pumps.Token);
        peer.RequestHandler = connection.HandleAsync;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Closed += _ => closed.TrySetResult();
        connection.StartIfTrusted();
        peer.Start();
        using var registration = cancellationToken.Register(() => closed.TrySetResult());
        await closed.Task.ConfigureAwait(false);
        await pumps.CancelAsync().ConfigureAwait(false);
        connection.StopAll();
        await connection.Uploads.DisposeAsync().ConfigureAwait(false);
    }

    private HostInfo StaticInfo() => new(
        HostProtocol.Version,
        ReleaseVersion,
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        RemoteHostEndpoint.DefaultRoot(),
        Capabilities);

    /** 一条连接的处理与推送状态。 */
    private sealed class Connection(RemoteHostServer server, JsonRpcPeer peer, string id, CancellationToken lifetime)
    {
        private const string PtyPrefix = "host.pty";
        private const string AgentPrefix = "host.agent";
        private const string GitPrefix = "host.git";
        private const string FilePrefix = "host.file";
        private const string FsPrefix = "host.fs";
        private const string NotifyPrefix = "host.notify";

        private readonly KeyedPumps _eventPumps = new(lifetime);
        private readonly KeyedPumps _ptyPumps = new(lifetime);
        private readonly List<HostSubscription> _subscriptions = [];
        private string? _clientName;

        /** With a host token every method but hello waits for a successful hello; without one the connection is trusted from the start. */
        private volatile bool _authenticated = !server.RequiresToken;

        private PendingResolver Resolver => new(_clientName ?? id, id);

        /** Chunked uploads started on this connection; discarded when it ends. */
        public HostUploads Uploads { get; } = new();

        /** 无 token 的 host 从连接建立起就推送; 有 token 的等 hello 通过后再推, 未认证的连接收不到任何请求内容。 */
        public void StartIfTrusted()
        {
            if (_authenticated)
                StartHostPushes();
        }

        public void StopAll()
        {
            _eventPumps.StopAll();
            _ptyPumps.StopAll();
            lock (_subscriptions)
            {
                foreach (var subscription in _subscriptions)
                    subscription.Dispose();
                _subscriptions.Clear();
            }
        }

        public async ValueTask<JsonElement?> HandleAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            var name = message.Method ?? "";
            if (!_authenticated && name != HostProtocol.MethodHello)
                throw new JsonRpcException(NotAuthenticated, $"call {HostProtocol.MethodHello} with the host token before {name}");
            switch (name)
            {
                case HostProtocol.MethodHello:
                    return Serialize(Hello(message), HostProtocolJsonContext.Default.HostHelloResponse);
                case HostProtocol.MethodPing:
                    return Serialize(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), HostProtocolJsonContext.Default.Int64);
                case HostProtocol.MethodInfo:
                    return Serialize(await InfoAsync(cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.HostInfo);
            }
            var backend = server._backend ?? throw MethodNotFound(name);
            return Family(name) switch
            {
                PtyPrefix => await HandlePtyAsync(backend, name, message, cancellationToken).ConfigureAwait(false),
                AgentPrefix => await HostAgentMethods.HandleAsync(backend, name, message, cancellationToken).ConfigureAwait(false),
                GitPrefix => await HostGitMethods.HandleAsync(backend.Files.Policy, name, message, cancellationToken).ConfigureAwait(false),
                FilePrefix or FsPrefix => await HostFileMethods.HandleAsync(backend.Files, Uploads, name, message, cancellationToken).ConfigureAwait(false),
                NotifyPrefix => await HostNotifyMethods.HandleAsync(backend.Notifications, name, message, cancellationToken).ConfigureAwait(false),
                _ => await HandleBackendAsync(backend, name, message, cancellationToken).ConfigureAwait(false),
            };
        }

        /** The method family: the first two dot-separated segments ("host.fs" of "host.fs.upload.begin"). */
        private static string Family(string name)
        {
            var second = name.IndexOf('.', name.IndexOf('.') + 1);
            return second < 0 ? name : name[..second];
        }

        private HostHelloResponse Hello(JsonRpcMessage message)
        {
            var request = message.Params is { } parameters
                ? JsonSerializer.Deserialize(parameters, HostProtocolJsonContext.Default.HostHelloRequest)
                : null;
            var token = server.Token;
            var tokenOk = token is null
                || (request?.Token is { } presented
                    && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token)));
            var versionOk = request?.ProtocolVersion == HostProtocol.Version;
            var mismatch = !tokenOk
                ? "invalid token"
                : (!versionOk
                    ? $"protocol version mismatch: client {request?.ProtocolVersion}, host {HostProtocol.Version}"
                    : null);
            if (mismatch is null)
                Authenticate(request?.ClientName);
            return new HostHelloResponse(mismatch is null, HostProtocol.Version, ReleaseVersion, mismatch, server.Capabilities, id);
        }

        private void Authenticate(string? clientName)
        {
            _clientName = string.IsNullOrWhiteSpace(clientName) ? null : clientName;
            if (_authenticated)
                return;
            _authenticated = true;
            StartHostPushes();
        }

        private async Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
            => server._backend is { } backend
                ? (await backend.InfoAsync(cancellationToken).ConfigureAwait(false)) with { Capabilities = server.Capabilities }
                : server.StaticInfo();

        private async ValueTask<JsonElement?> HandleBackendAsync(IRemoteHostBackend backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
        {
            switch (name)
            {
                case HostProtocol.MethodSessionList:
                    return Serialize(await backend.ListSessionsAsync(cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.IReadOnlyListRemoteSessionInfo);
                case HostProtocol.MethodSessionCreate:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteSessionCreateRequest, "create requires cwd");
                    return Serialize(await backend.CreateSessionAsync(request.Cwd, request.Selection, request.Provider, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteSessionInfo);
                }
                case HostProtocol.MethodSessionResume:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteSessionRef, "resume requires sessionId");
                    return Serialize(await backend.ResumeSessionAsync(request.SessionId, request.Selection, request.Provider, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteSessionInfo);
                }
                case HostProtocol.MethodSessionSubscribe:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteSubscribeRequest, "subscribe requires sessionId");
                    var events = await backend.SubscribeAsync(request.SessionId, request.FromSeq, lifetime).ConfigureAwait(false);
                    _eventPumps.Start(request.SessionId, token => PumpAsync(events, HostProtocol.NotificationEvent, HostProtocolJsonContext.Default.RemoteEventInfo, token));
                    return True;
                }
                case HostProtocol.MethodSessionUnsubscribe:
                    _eventPumps.Stop(Require(message, HostProtocolJsonContext.Default.RemoteSessionRef, "unsubscribe requires sessionId").SessionId);
                    return True;
                case HostProtocol.MethodSessionMessage:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteMessageRequest, "message requires sessionId and text");
                    await backend.SendMessageAsync(request.SessionId, request.Text, request.Images ?? [], cancellationToken).ConfigureAwait(false);
                    return True;
                }
                case HostProtocol.MethodSessionInterrupt:
                    await backend.InterruptAsync(Require(message, HostProtocolJsonContext.Default.RemoteSessionRef, "interrupt requires sessionId").SessionId, cancellationToken).ConfigureAwait(false);
                    return True;
                case HostProtocol.MethodSessionsList:
                    return Serialize(backend.Sessions.List(), HostProtocolJsonContext.Default.IReadOnlyListRemoteHostSession);
                case HostProtocol.MethodToolsList:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteSessionRef, "tools requires sessionId");
                    return Serialize(await backend.ListToolsAsync(request.SessionId, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.IReadOnlyListString);
                }
                default:
                    return HandleDecisions(backend, name, message);
            }
        }

        private JsonElement HandleDecisions(IRemoteHostBackend backend, string name, JsonRpcMessage message)
        {
            switch (name)
            {
                case HostProtocol.MethodApprovalRespond:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteApprovalResponse, "approval requires requestId");
                    return Serialize(backend.Pending.RespondApproval(request, Resolver), HostProtocolJsonContext.Default.Boolean);
                }
                case HostProtocol.MethodQuestionRespond:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteQuestionResponse, "question respond requires requestId");
                    return Serialize(backend.Pending.RespondQuestion(request, Resolver), HostProtocolJsonContext.Default.Boolean);
                }
                case HostProtocol.MethodPendingList:
                    return Serialize(backend.Pending.List(), HostProtocolJsonContext.Default.RemotePendingList);
                default:
                    throw MethodNotFound(name);
            }
        }

        private async ValueTask<JsonElement?> HandlePtyAsync(IRemoteHostBackend backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
        {
            switch (name)
            {
                case HostProtocol.MethodPtyStart:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemotePtyStartRequest, "pty start requires fileName");
                    var ptyId = await backend.StartPtyAsync(request, cancellationToken).ConfigureAwait(false);
                    return Serialize(new RemotePtyRef(ptyId), HostProtocolJsonContext.Default.RemotePtyRef);
                }
                case HostProtocol.MethodPtyWrite:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemotePtyInput, "pty write requires ptyId");
                    await backend.WritePtyAsync(request.PtyId, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);
                    return True;
                }
                case HostProtocol.MethodPtyResize:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemotePtyResize, "pty resize requires ptyId, columns and rows");
                    await backend.ResizePtyAsync(request.PtyId, request.Columns, request.Rows, cancellationToken).ConfigureAwait(false);
                    return True;
                }
                case HostProtocol.MethodPtyStop:
                    await backend.StopPtyAsync(Require(message, HostProtocolJsonContext.Default.RemotePtyRef, "pty stop requires ptyId").PtyId, cancellationToken).ConfigureAwait(false);
                    return True;
                case HostProtocol.MethodPtyAttach:
                {
                    var ptyId = Require(message, HostProtocolJsonContext.Default.RemotePtyRef, "pty attach requires ptyId").PtyId;
                    var output = await backend.AttachPtyAsync(ptyId, lifetime).ConfigureAwait(false);
                    _ptyPumps.Start(ptyId, token => PumpAsync(output, HostProtocol.NotificationPtyOutput, HostProtocolJsonContext.Default.RemotePtyOutput, token));
                    return True;
                }
                case HostProtocol.MethodPtyDetach:
                    _ptyPumps.Stop(Require(message, HostProtocolJsonContext.Default.RemotePtyRef, "pty detach requires ptyId").PtyId);
                    return True;
                default:
                    throw new JsonRpcException(JsonRpcPeer.MethodNotFound, $"method not found: {name}");
            }
        }

        private void StartHostPushes()
        {
            if (server._backend is not { } backend)
                return;
            HostSubscription[] subscriptions = [backend.Pending.Subscribe(), backend.Sessions.Subscribe(), backend.Notifications.Subscribe()];
            lock (_subscriptions)
                _subscriptions.AddRange(subscriptions);
            foreach (var subscription in subscriptions)
                _ = PumpNotificationsAsync(subscription);
        }

        private async Task PumpNotificationsAsync(HostSubscription subscription)
        {
            try
            {
                await foreach (var notification in subscription.Reader.ReadAllAsync(lifetime).ConfigureAwait(false))
                    await peer.NotifyAsync(notification.Method, notification.Params, lifetime).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        private async Task PumpAsync<T>(IAsyncEnumerable<T> items, string method, JsonTypeInfo<T> info, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
                    await peer.NotifyAsync(method, Serialize(item, info), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    /** 每个 key(会话 id / PTY id)至多一个推送泵: 重复订阅先停旧的, 避免同一内容被推送多次。 */
    private sealed class KeyedPumps(CancellationToken lifetime)
    {
        private readonly Dictionary<string, CancellationTokenSource> _pumps = new(StringComparer.Ordinal);

        public void Start(string key, Func<CancellationToken, Task> pump)
        {
            CancellationTokenSource source;
            lock (_pumps)
            {
                if (_pumps.Remove(key, out var existing))
                    existing.Cancel();
                source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                _pumps[key] = source;
            }
            _ = pump(source.Token);
        }

        public void Stop(string key)
        {
            CancellationTokenSource? source;
            lock (_pumps)
                _pumps.Remove(key, out source);
            source?.Cancel();
        }

        public void StopAll()
        {
            CancellationTokenSource[] sources;
            lock (_pumps)
            {
                sources = [.. _pumps.Values];
                _pumps.Clear();
            }
            foreach (var source in sources)
                source.Cancel();
        }
    }
}
