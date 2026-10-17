using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Dsh.GitInspect;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/**
 * 本地侧连接远端 host: 先握手(版本/token), 然后实现 IRemoteHost。
 * 会话事件/审批/PTY 输出经服务端推送(通知)转成本端的异步流。
 */
public sealed class RemoteHostClient : IRemoteHost, IAsyncDisposable
{
    /** 只用于观察的推送流(会话变化/请求结局)没人消费时最多积压的条数, 超出丢最旧的; 审批/问答流不丢。 */
    private const int ObservationBacklog = 1024;

    private readonly JsonRpcPeer _peer;
    private readonly ConcurrentDictionary<string, Channel<RemoteEventInfo>> _eventStreams = new(StringComparer.Ordinal);
    private readonly Channel<RemotePendingChange> _pendingChanges = Channel.CreateUnbounded<RemotePendingChange>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<RemoteSessionChange> _sessionChanges = ObservationChannel<RemoteSessionChange>();

    /** 推送处理在握手之前挂好: host 在 hello 应答之后立即补发未决请求, 晚挂会漏掉它们。 */
    private RemoteHostClient(JsonRpcPeer peer)
    {
        _peer = peer;
        Agent = new RemoteAgentClient(this);
        Pty = new RemotePtyClient(this);
        Files = new RemoteFilesClient(this);
        Git = new RemoteGitClient(this);
        Notifications = new RemoteNotificationsClient(this);
        SessionEditing = new RemoteSessionEditingClient(this);
        Plugins = new RemotePluginsClient(this);
        Models = new RemoteModelsClient(this);
        peer.NotificationReceived += OnNotification;
        peer.Closed += OnClosed;
    }

    public HostHelloResponse Hello { get; private set; } = new(false, HostProtocol.Version, "", null);
    public RemoteAgentClient Agent { get; }
    public RemotePtyClient Pty { get; }
    public RemoteFilesClient Files { get; }
    public RemoteGitClient Git { get; }
    public RemoteNotificationsClient Notifications { get; }
    public RemoteSessionEditingClient SessionEditing { get; }
    public RemotePluginsClient Plugins { get; }
    public RemoteModelsClient Models { get; }
    internal bool IsClosed => _peer.IsClosed;

    /** Whether the host advertises a capability from HostProtocolCatalog. */
    public bool Supports(string capability) => Hello.Capabilities?.Contains(capability) == true;

    public Task WhenClosed => _closed.Task;

    public async Task<JsonElement> RequestAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
        => await _peer.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"host returned no result for {method}");

    public static Task<RemoteHostClient> ConnectAsync(
        Stream duplex,
        string? token = null,
        CancellationToken cancellationToken = default)
        => ConnectAsync(duplex, token, clientName: null, cancellationToken);

    /** ClientName 是本客户端的显示名(如 "gui@laptop"), host 用它标注由谁回答了审批/问答。 */
    public static async Task<RemoteHostClient> ConnectAsync(
        Stream duplex,
        string? token,
        string? clientName,
        CancellationToken cancellationToken)
    {
        var peer = new JsonRpcPeer(duplex);
        var client = new RemoteHostClient(peer);
        peer.Start();
        try
        {
            var request = new HostHelloRequest(token, HostProtocol.Version, clientName);
            var parameters = JsonSerializer.SerializeToElement(request, HostProtocolJsonContext.Default.HostHelloRequest);
            var result = await peer.RequestAsync(HostProtocol.MethodHello, parameters, cancellationToken).ConfigureAwait(false);
            var hello = result is { } value
                ? JsonSerializer.Deserialize(value, HostProtocolJsonContext.Default.HostHelloResponse)
                : null;
            if (hello is null)
                throw new IOException("host returned an invalid hello");
            if (!hello.Ok)
                throw new IOException($"host handshake rejected: {hello.Mismatch}");
            if (hello.ProtocolVersion != HostProtocol.Version)
                throw new IOException($"host protocol version mismatch: expected {HostProtocol.Version}, received {hello.ProtocolVersion}");
            client.Hello = hello;
            return client;
        }
        catch
        {
            await peer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static Channel<T> ObservationChannel<T>()
        => Channel.CreateBounded<T>(new BoundedChannelOptions(ObservationBacklog) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public Task<long> PingAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodPing, null, HostProtocolJsonContext.Default.Int64, cancellationToken);

    public Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodInfo, null, HostProtocolJsonContext.Default.HostInfo, cancellationToken);

    public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionList, null, HostProtocolJsonContext.Default.IReadOnlyListRemoteSessionInfo, cancellationToken);

    public Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection = null, RemoteProviderSpec? provider = null, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionCreate, Serialize(new RemoteSessionCreateRequest(cwd, selection, provider), HostProtocolJsonContext.Default.RemoteSessionCreateRequest), HostProtocolJsonContext.Default.RemoteSessionInfo, cancellationToken);

    public Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, RemoteModelSelection? selection = null, RemoteProviderSpec? provider = null, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionResume, Serialize(new RemoteSessionRef(sessionId, selection, provider), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.RemoteSessionInfo, cancellationToken);

    public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionSnapshot, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.RemoteSessionSnapshot, cancellationToken);

    public Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default)
        => SessionEditing.ExecuteSessionCommandAsync(sessionId, line, cancellationToken);

    public Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken = default)
        => SessionEditing.ListSessionCommandsAsync(sessionId, cancellationToken);

    public Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default)
        => SessionEditing.RenameSessionAsync(sessionId, title, cancellationToken);

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => SessionEditing.DeleteSessionAsync(sessionId, cancellationToken);

    public Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken = default)
        => SessionEditing.SetSessionApprovalPolicyAsync(sessionId, policy, cancellationToken);

    public Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken = default)
        => SessionEditing.ListSubagentsAsync(rootSessionId, cancellationToken);

    public Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken = default)
        => Plugins.ListPluginsAsync(cancellationToken);

    public Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken = default)
        => Plugins.SetPluginPermissionAsync(package, permission, allowed, cancellationToken);

    public Task<IReadOnlyList<string>> ListReasoningEffortsAsync(string provider, string model, CancellationToken cancellationToken = default)
        => Models.ListReasoningEffortsAsync(provider, model, cancellationToken);

    public Task<IReadOnlyList<string>> ListProviderModelsAsync(string provider, CancellationToken cancellationToken = default)
        => Models.ListProviderModelsAsync(provider, cancellationToken);

    public async IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<RemoteEventInfo>(new UnboundedChannelOptions { SingleReader = true });
        _eventStreams[subscriptionId] = channel;
        try
        {
            await SendAsync(HostProtocol.MethodSessionSubscribe, Serialize(new RemoteSubscribeRequest(sessionId, fromSeq, subscriptionId), HostProtocolJsonContext.Default.RemoteSubscribeRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            _eventStreams.TryRemove(subscriptionId, out _);
            await StopStreamAsync(HostProtocol.MethodSessionUnsubscribe, subscriptionId).ConfigureAwait(false);
        }
    }

    public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionMessage, Serialize(new RemoteMessageRequest(sessionId, text, images), HostProtocolJsonContext.Default.RemoteMessageRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionInterrupt, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodToolsList, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.IReadOnlyListString, cancellationToken);

    public IAsyncEnumerable<RemotePendingChange> PendingChangesAsync(CancellationToken cancellationToken = default)
        => _pendingChanges.Reader.ReadAllAsync(cancellationToken);

    public Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default, bool rememberForSession = false)
        => SendAsync(HostProtocol.MethodApprovalRespond, Serialize(new RemoteApprovalResponse(requestId, allow, reason, rememberForSession), HostProtocolJsonContext.Default.RemoteApprovalResponse), HostProtocolJsonContext.Default.Boolean, cancellationToken);


    public Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodQuestionRespond, Serialize(new RemoteQuestionResponse(requestId, answers), HostProtocolJsonContext.Default.RemoteQuestionResponse), HostProtocolJsonContext.Default.Boolean, cancellationToken);


    public Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodPendingList, null, HostProtocolJsonContext.Default.RemotePendingList, cancellationToken);

    public Task<IReadOnlyList<RemoteHostSession>> ListHostSessionsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionsList, null, HostProtocolJsonContext.Default.IReadOnlyListRemoteHostSession, cancellationToken);

    public IAsyncEnumerable<RemoteSessionChange> HostSessionChangesAsync(CancellationToken cancellationToken = default)
        => _sessionChanges.Reader.ReadAllAsync(cancellationToken);

    public Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default)
        => Files.ReadFileAsync(path, cancellationToken);

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => Files.WriteFileAsync(path, content, cancellationToken);

    public Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Files.ListDirectoryAsync(path, cancellationToken);

    public Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken = default)
        => Pty.StartPtyAsync(request, cancellationToken);

    public Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken = default)
        => Pty.AttachPtyAsync(ptyId, input, output, cancellationToken);

    public Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken = default)
        => Pty.ResizePtyAsync(ptyId, columns, rows, cancellationToken);

    public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken = default)
        => Pty.StopPtyAsync(ptyId, cancellationToken);

    public Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken = default)
        => Agent.AgentCatalogAsync(cancellationToken);

    public Task<RemoteHostSession> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken = default)
        => Agent.StartAgentAsync(request, cancellationToken);

    public Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken = default)
        => Agent.PromptAgentAsync(sessionId, text, cancellationToken);

    public Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => Agent.CancelAgentAsync(sessionId, cancellationToken);

    public Task StopAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => Agent.StopAgentAsync(sessionId, cancellationToken);

    public Task<RemoteHostSession> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => Agent.ResumeAgentAsync(sessionId, cancellationToken);

    public Task<GitStatus> GitStatusAsync(string projectPath, CancellationToken cancellationToken = default)
        => Git.GitStatusAsync(projectPath, cancellationToken);

    public Task<GitDiff> GitDiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken = default)
        => Git.GitDiffAsync(request, cancellationToken);

    public Task<IReadOnlyList<GitCommitSummary>> GitLogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken = default)
        => Git.GitLogAsync(request, cancellationToken);

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string projectPath, CancellationToken cancellationToken = default)
        => Git.ListWorktreesAsync(projectPath, cancellationToken);

    public Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken = default)
        => Git.AddWorktreeAsync(request, cancellationToken);

    public Task RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken = default)
        => Git.RemoveWorktreeAsync(request, cancellationToken);

    public Task<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken = default)
        => Files.StatAsync(path, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Files.CreateDirectoryAsync(path, cancellationToken);

    public Task RenameAsync(string from, string to, bool overwrite = false, CancellationToken cancellationToken = default)
        => Files.RenameAsync(from, to, overwrite, cancellationToken);

    public Task DeleteAsync(string path, bool recursive = false, CancellationToken cancellationToken = default)
        => Files.DeleteAsync(path, recursive, cancellationToken);

    public Task<RemoteUploadRef> BeginUploadAsync(string path, bool overwrite = true, CancellationToken cancellationToken = default)
        => Files.BeginUploadAsync(path, overwrite, cancellationToken);

    public Task<long> UploadChunkAsync(string uploadId, long offset, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => Files.UploadChunkAsync(uploadId, offset, content, cancellationToken);

    public Task<RemoteFileStat> CommitUploadAsync(string uploadId, long? size = null, CancellationToken cancellationToken = default)
        => Files.CommitUploadAsync(uploadId, size, cancellationToken);

    public Task AbortUploadAsync(string uploadId, CancellationToken cancellationToken = default)
        => Files.AbortUploadAsync(uploadId, cancellationToken);

    public Task<RemoteFileChunk> DownloadChunkAsync(string path, long offset, int? length = null, CancellationToken cancellationToken = default)
        => Files.DownloadChunkAsync(path, offset, length, cancellationToken);

    public Task<RemoteFileStat> UploadFileAsync(string path, Stream source, bool overwrite = true, CancellationToken cancellationToken = default)
        => Files.UploadFileAsync(path, source, overwrite, cancellationToken);

    public Task<long> DownloadFileAsync(string path, Stream destination, CancellationToken cancellationToken = default)
        => Files.DownloadFileAsync(path, destination, cancellationToken);

    public Task<RemoteNotifyPage> ListNotificationsAsync(RemoteNotifyQuery? query = null, CancellationToken cancellationToken = default)
        => Notifications.ListNotificationsAsync(query, cancellationToken);

    public Task<int> MarkNotificationsReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken = default)
        => Notifications.MarkNotificationsReadAsync(request, cancellationToken);

    public IAsyncEnumerable<RemoteNotificationChange> NotificationChangesAsync(CancellationToken cancellationToken = default)
        => Notifications.NotificationChangesAsync(cancellationToken);


    private async Task StopStreamAsync(string method, string subscriptionId)
    {
        if (_peer.IsClosed)
            return;
        try
        {
            await SendAsync(method, Serialize(new RemoteSubscriptionRef(subscriptionId), HostProtocolJsonContext.Default.RemoteSubscriptionRef), HostProtocolJsonContext.Default.Boolean, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
        }
    }

    private void OnNotification(JsonRpcMessage message)
    {
        Pty.OnNotification(message);
        Notifications.OnNotification(message);
        switch (message.Method)
        {
            case HostProtocol.NotificationEvent when message.Params is { } eventParameters:
                var frame = JsonSerializer.Deserialize(eventParameters, HostProtocolJsonContext.Default.RemoteEventFrame);
                if (_eventStreams.TryGetValue(frame.SubscriptionId, out var events))
                    events.Writer.TryWrite(frame.Event);
                break;
            case HostProtocol.NotificationPendingChanged when Parse(message, HostProtocolJsonContext.Default.RemotePendingChange) is { } pending:
                _pendingChanges.Writer.TryWrite(pending);
                break;
            case HostProtocol.NotificationSessionsChanged when Parse(message, HostProtocolJsonContext.Default.RemoteSessionChange) is { } session:
                _sessionChanges.Writer.TryWrite(session);
                break;
        }
    }

    private void OnClosed(Exception? error)
    {
        var failure = error ?? new IOException("host connection closed");
        Pty.OnClosed(failure);
        Notifications.OnClosed(failure);
        foreach (var stream in _eventStreams.Values)
            stream.Writer.TryComplete(failure);
        _pendingChanges.Writer.TryComplete(failure);
        _sessionChanges.Writer.TryComplete(failure);
        _closed.TrySetResult();
    }

    private static T? Parse<T>(JsonRpcMessage message, JsonTypeInfo<T> info)
        => message.Params is { } element ? JsonSerializer.Deserialize(element, info) : default;

    private static JsonElement Serialize<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToElement(value, info);

    internal async Task<T> SendAsync<T>(string method, JsonElement? parameters, JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var value = await RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(value, info) ?? throw new IOException($"host returned an invalid result for {method}");
    }

    public ValueTask DisposeAsync() => _peer.DisposeAsync();
}
