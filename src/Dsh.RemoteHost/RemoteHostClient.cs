using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Dsh.GitInspect;
using Dsh.Notifications;
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
    private readonly ConcurrentDictionary<string, Channel<RemotePtyOutput>> _ptyStreams = new(StringComparer.Ordinal);
    private readonly Channel<RemotePendingChange> _pendingChanges = Channel.CreateUnbounded<RemotePendingChange>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<RemoteHostSession> _sessionChanges = ObservationChannel<RemoteHostSession>();
    private readonly Channel<NotificationRecord> _notificationsAdded = ObservationChannel<NotificationRecord>();
    private readonly Channel<RemoteNotifyChanged> _notificationChanges = ObservationChannel<RemoteNotifyChanged>();

    /** 推送处理在握手之前挂好: host 在 hello 应答之后立即补发未决请求, 晚挂会漏掉它们。 */
    private RemoteHostClient(JsonRpcPeer peer)
    {
        _peer = peer;
        peer.NotificationReceived += OnNotification;
        peer.Closed += OnClosed;
    }

    public HostHelloResponse Hello { get; private set; } = new(false, HostProtocol.Version, "", null);

    /** 对端是否声明了某项能力(见 HostCapabilities); 旧宿主不声明任何能力。 */
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
            client.Hello = hello;
            return client;
        }
        catch
        {
            await peer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Channel<T> ObservationChannel<T>()
        => Channel.CreateBounded<T>(new BoundedChannelOptions(ObservationBacklog) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public Task<long> PingAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodPing, null, HostProtocolJsonContext.Default.Int64, cancellationToken);

    public Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodInfo, null, HostProtocolJsonContext.Default.HostInfo, cancellationToken);

    public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionList, null, HostProtocolJsonContext.Default.IReadOnlyListRemoteSessionInfo, cancellationToken);

    public Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection = null, RemoteProviderSpec? provider = null, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionCreate, Serialize(new RemoteSessionCreateRequest(cwd, selection, provider), HostProtocolJsonContext.Default.RemoteSessionCreateRequest), HostProtocolJsonContext.Default.RemoteSessionInfo, cancellationToken);

    public async Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, RemoteModelSelection? selection = null, RemoteProviderSpec? provider = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendAsync(HostProtocol.MethodSessionResume, Serialize(new RemoteSessionRef(sessionId, selection, provider), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.RemoteSessionInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcException error) when (error.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            // 老版本宿主对已在跑的会话会拒绝 resume(非幂等); 视为成功, 从会话列表取信息。
            var sessions = await ListSessionsAsync(cancellationToken).ConfigureAwait(false);
            return sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal))
                ?? new RemoteSessionInfo(sessionId, "", "Running", 0, null, null, 0);
        }
    }

    public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionSnapshot, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.RemoteSessionSnapshot, cancellationToken);

    public Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionCommand, Serialize(new RemoteCommandRequest(sessionId, line), HostProtocolJsonContext.Default.RemoteCommandRequest), HostProtocolJsonContext.Default.RemoteCommandResult, cancellationToken);

    public Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionCommands, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.IReadOnlyListRemoteCommandDescriptor, cancellationToken);

    public Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionRename, Serialize(new RemoteSessionRenameRequest(sessionId, title), HostProtocolJsonContext.Default.RemoteSessionRenameRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionDelete, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionApprovalPolicy, Serialize(new RemoteSessionPolicyRequest(sessionId, policy), HostProtocolJsonContext.Default.RemoteSessionPolicyRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionSubagents, Serialize(new RemoteSessionRef(rootSessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.IReadOnlyListRemoteSubagentInfo, cancellationToken);

    public Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodPluginsList, null, HostProtocolJsonContext.Default.IReadOnlyListRemotePluginInfo, cancellationToken);

    public Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodPluginPermission, Serialize(new RemotePluginPermissionRequest(package, permission, allowed), HostProtocolJsonContext.Default.RemotePluginPermissionRequest), HostProtocolJsonContext.Default.String, cancellationToken);

    public Task<IReadOnlyList<string>> ListReasoningEffortsAsync(string provider, string model, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodReasoningEfforts, Serialize(new RemoteModelRef(provider, model), HostProtocolJsonContext.Default.RemoteModelRef), HostProtocolJsonContext.Default.IReadOnlyListString, cancellationToken);

    public Task<IReadOnlyList<string>> ListProviderModelsAsync(string provider, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodProviderModels, Serialize(new RemoteProviderRef(provider), HostProtocolJsonContext.Default.RemoteProviderRef), HostProtocolJsonContext.Default.IReadOnlyListString, cancellationToken);

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

    public IAsyncEnumerable<RemoteHostSession> HostSessionChangesAsync(CancellationToken cancellationToken = default)
        => _sessionChanges.Reader.ReadAllAsync(cancellationToken);

    public async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var content = await SendAsync(HostProtocol.MethodFileRead, Serialize(new RemoteFilePath(path), HostProtocolJsonContext.Default.RemoteFilePath), HostProtocolJsonContext.Default.RemoteFileContent, cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(content.Base64);
    }

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFileWrite, Serialize(new RemoteFileContent(path, Convert.ToBase64String(content.Span)), HostProtocolJsonContext.Default.RemoteFileContent), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFileList, Serialize(new RemoteFilePath(path), HostProtocolJsonContext.Default.RemoteFilePath), HostProtocolJsonContext.Default.RemoteDirectoryListing, cancellationToken);

    public async Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken = default)
    {
        var reference = await SendAsync(HostProtocol.MethodPtyStart, Serialize(request, HostProtocolJsonContext.Default.RemotePtyStartRequest), HostProtocolJsonContext.Default.RemotePtyRef, cancellationToken).ConfigureAwait(false);
        return reference.PtyId;
    }

    /** 每个视图单独订阅同一 PTY，结束时只释放自己的推送。 */
    public async Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<RemotePtyOutput>(new UnboundedChannelOptions { SingleReader = true });
        _ptyStreams[subscriptionId] = channel;
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pumpInput = Task.CompletedTask;
        try
        {
            await SendAsync(HostProtocol.MethodPtyAttach, Serialize(new RemotePtyAttachRequest(ptyId, subscriptionId), HostProtocolJsonContext.Default.RemotePtyAttachRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
            pumpInput = PumpPtyInputAsync(ptyId, input, inputLifetime.Token);
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var bytes = Convert.FromBase64String(item.Base64);
                if (bytes.Length > 0)
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (item.Eof)
                    break;
            }
        }
        finally
        {
            _ptyStreams.TryRemove(subscriptionId, out _);
            await inputLifetime.CancelAsync().ConfigureAwait(false);
            await StopStreamAsync(HostProtocol.MethodPtyDetach, subscriptionId).ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await pumpInput.ConfigureAwait(false);
        }
    }

    public Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken = default)
        => Supports(HostCapabilities.PtyResize)
            ? SendAsync(HostProtocol.MethodPtyResize, Serialize(new RemotePtyResize(ptyId, columns, rows), HostProtocolJsonContext.Default.RemotePtyResize), HostProtocolJsonContext.Default.Boolean, cancellationToken)
            : Task.CompletedTask;

    public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodPtyStop, Serialize(new RemotePtyRef(ptyId), HostProtocolJsonContext.Default.RemotePtyRef), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodAgentCatalog, null, HostProtocolJsonContext.Default.IReadOnlyListRemoteAgentCatalogEntry, cancellationToken);

    public Task<RemoteHostSession> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodAgentStart, Serialize(request, HostProtocolJsonContext.Default.RemoteAgentStartRequest), HostProtocolJsonContext.Default.RemoteHostSession, cancellationToken);

    public Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodAgentPrompt, Serialize(new RemoteAgentPrompt(sessionId, text), HostProtocolJsonContext.Default.RemoteAgentPrompt), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodAgentCancel, SessionRef(sessionId), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task StopAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodAgentStop, SessionRef(sessionId), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<RemoteHostSession> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodAgentResume, SessionRef(sessionId), HostProtocolJsonContext.Default.RemoteHostSession, cancellationToken);

    public Task<GitStatus> GitStatusAsync(string projectPath, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodGitStatus, Serialize(new RemoteGitRequest(projectPath), HostProtocolJsonContext.Default.RemoteGitRequest), HostProtocolJsonContext.Default.GitStatus, cancellationToken);

    public Task<GitDiff> GitDiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodGitDiff, Serialize(request, HostProtocolJsonContext.Default.RemoteGitDiffRequest), HostProtocolJsonContext.Default.GitDiff, cancellationToken);

    public Task<IReadOnlyList<GitCommitSummary>> GitLogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodGitLog, Serialize(request, HostProtocolJsonContext.Default.RemoteGitLogRequest), HostProtocolJsonContext.Default.IReadOnlyListGitCommitSummary, cancellationToken);

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string projectPath, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodGitWorktreeList, Serialize(new RemoteGitRequest(projectPath), HostProtocolJsonContext.Default.RemoteGitRequest), HostProtocolJsonContext.Default.IReadOnlyListGitWorktree, cancellationToken);

    public Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodGitWorktreeAdd, Serialize(request, HostProtocolJsonContext.Default.RemoteGitWorktreeAdd), HostProtocolJsonContext.Default.GitWorktree, cancellationToken);

    public Task RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodGitWorktreeRemove, Serialize(request, HostProtocolJsonContext.Default.RemoteGitWorktreeRemove), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFsStat, FilePath(path), HostProtocolJsonContext.Default.RemoteFileStat, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFsMkdir, FilePath(path), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task RenameAsync(string from, string to, bool overwrite = false, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFsRename, Serialize(new RemoteFsRename(from, to, overwrite), HostProtocolJsonContext.Default.RemoteFsRename), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task DeleteAsync(string path, bool recursive = false, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFsDelete, Serialize(new RemoteFsDelete(path, recursive), HostProtocolJsonContext.Default.RemoteFsDelete), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<RemoteUploadRef> BeginUploadAsync(string path, bool overwrite = true, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFsUploadBegin, Serialize(new RemoteUploadBegin(path, overwrite), HostProtocolJsonContext.Default.RemoteUploadBegin), HostProtocolJsonContext.Default.RemoteUploadRef, cancellationToken);

    public Task<long> UploadChunkAsync(string uploadId, long offset, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => SendAsync(
            HostProtocol.MethodFsUploadChunk,
            Serialize(new RemoteUploadChunk(uploadId, offset, Convert.ToBase64String(content.Span)), HostProtocolJsonContext.Default.RemoteUploadChunk),
            HostProtocolJsonContext.Default.Int64,
            cancellationToken);

    public Task<RemoteFileStat> CommitUploadAsync(string uploadId, long? size = null, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFsUploadCommit, Serialize(new RemoteUploadCommit(uploadId, size), HostProtocolJsonContext.Default.RemoteUploadCommit), HostProtocolJsonContext.Default.RemoteFileStat, cancellationToken);

    public Task AbortUploadAsync(string uploadId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFsUploadAbort, Serialize(new RemoteUploadRef(uploadId), HostProtocolJsonContext.Default.RemoteUploadRef), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<RemoteFileChunk> DownloadChunkAsync(string path, long offset, int? length = null, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFsDownload, Serialize(new RemoteDownloadRequest(path, offset, length), HostProtocolJsonContext.Default.RemoteDownloadRequest), HostProtocolJsonContext.Default.RemoteFileChunk, cancellationToken);

    public async Task<RemoteFileStat> UploadFileAsync(string path, Stream source, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        var upload = await BeginUploadAsync(path, overwrite, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[upload.ChunkBytes];
        long sent = 0;
        try
        {
            int read;
            while ((read = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)) > 0)
                sent = await UploadChunkAsync(upload.UploadId, sent, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            return await CommitUploadAsync(upload.UploadId, sent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortUploadQuietlyAsync(upload.UploadId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<long> DownloadFileAsync(string path, Stream destination, CancellationToken cancellationToken = default)
    {
        long offset = 0;
        while (true)
        {
            var chunk = await DownloadChunkAsync(path, offset, null, cancellationToken).ConfigureAwait(false);
            var bytes = Convert.FromBase64String(chunk.Base64);
            await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            offset += bytes.Length;
            if (chunk.Eof || bytes.Length == 0)
                return offset;
        }
    }

    public Task<RemoteNotifyPage> ListNotificationsAsync(RemoteNotifyQuery? query = null, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodNotifyList, Serialize(query ?? new RemoteNotifyQuery(), HostProtocolJsonContext.Default.RemoteNotifyQuery), HostProtocolJsonContext.Default.RemoteNotifyPage, cancellationToken);

    public Task<int> MarkNotificationsReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodNotifyMarkRead, Serialize(request, HostProtocolJsonContext.Default.RemoteNotifyMarkRead), HostProtocolJsonContext.Default.Int32, cancellationToken);

    public IAsyncEnumerable<NotificationRecord> NotificationsAddedAsync(CancellationToken cancellationToken = default)
        => _notificationsAdded.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<RemoteNotifyChanged> NotificationChangesAsync(CancellationToken cancellationToken = default)
        => _notificationChanges.Reader.ReadAllAsync(cancellationToken);

    /** The upload is already gone when the host rejected it; a closed connection discards it on the host side anyway. */
    private async Task AbortUploadQuietlyAsync(string uploadId)
    {
        if (_peer.IsClosed)
            return;
        try
        {
            await AbortUploadAsync(uploadId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or ObjectDisposedException)
        {
        }
    }

    private static JsonElement SessionRef(string sessionId) => Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef);

    private static JsonElement FilePath(string path) => Serialize(new RemoteFilePath(path), HostProtocolJsonContext.Default.RemoteFilePath);

    private async Task PumpPtyInputAsync(string ptyId, Stream input, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                await SendAsync(HostProtocol.MethodPtyWrite, Serialize(new RemotePtyInput(ptyId, Convert.ToBase64String(buffer.AsSpan(0, read))), HostProtocolJsonContext.Default.RemotePtyInput), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
        }
    }

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
            case HostProtocol.NotificationSessionsChanged when Parse(message, HostProtocolJsonContext.Default.RemoteHostSession) is { } session:
                _sessionChanges.Writer.TryWrite(session);
                break;
            case HostProtocol.NotificationNotifyAdded when Parse(message, HostProtocolJsonContext.Default.NotificationRecord) is { } added:
                _notificationsAdded.Writer.TryWrite(added);
                break;
            case HostProtocol.NotificationNotifyChanged when Parse(message, HostProtocolJsonContext.Default.RemoteNotifyChanged) is { } changed:
                _notificationChanges.Writer.TryWrite(changed);
                break;
            case HostProtocol.NotificationPtyOutput when message.Params is { } ptyParameters:
                var ptyFrame = JsonSerializer.Deserialize(ptyParameters, HostProtocolJsonContext.Default.RemotePtyFrame);
                if (_ptyStreams.TryGetValue(ptyFrame.SubscriptionId, out var pty))
                    pty.Writer.TryWrite(ptyFrame.Output);
                break;
        }
    }

    private void OnClosed(Exception? error)
    {
        var failure = error ?? new IOException("host connection closed");
        foreach (var stream in _eventStreams.Values)
            stream.Writer.TryComplete(failure);
        foreach (var stream in _ptyStreams.Values)
            stream.Writer.TryComplete(failure);
        _pendingChanges.Writer.TryComplete(failure);
        _sessionChanges.Writer.TryComplete(failure);
        _notificationsAdded.Writer.TryComplete(failure);
        _notificationChanges.Writer.TryComplete(failure);
        _closed.TrySetResult();
    }

    private static T? Parse<T>(JsonRpcMessage message, JsonTypeInfo<T> info)
        => message.Params is { } element ? JsonSerializer.Deserialize(element, info) : default;

    private static JsonElement Serialize<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToElement(value, info);

    private async Task<T> SendAsync<T>(string method, JsonElement? parameters, JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var value = await RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(value, info) ?? throw new IOException($"host returned an invalid result for {method}");
    }

    public ValueTask DisposeAsync() => _peer.DisposeAsync();
}
