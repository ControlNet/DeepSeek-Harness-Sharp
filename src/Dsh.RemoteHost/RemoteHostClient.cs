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
    private readonly Channel<RemoteApprovalRequest> _approvals = Channel.CreateUnbounded<RemoteApprovalRequest>();
    private readonly Channel<RemoteQuestionRequest> _questions = Channel.CreateUnbounded<RemoteQuestionRequest>();
    private readonly Channel<RemotePendingResolved> _resolutions = ObservationChannel<RemotePendingResolved>();
    private readonly Channel<RemoteHostSession> _sessionChanges = ObservationChannel<RemoteHostSession>();
    private readonly Channel<NotificationRecord> _notificationsAdded = ObservationChannel<NotificationRecord>();
    private readonly Channel<RemoteNotifyChanged> _notificationChanges = ObservationChannel<RemoteNotifyChanged>();

    /** 推送处理在握手之前挂好: host 在 hello 应答之后立即补发未决请求, 晚挂会漏掉它们。 */
    private RemoteHostClient(JsonRpcPeer peer)
    {
        _peer = peer;
        peer.NotificationReceived += OnNotification;
    }

    public HostHelloResponse Hello { get; private set; } = new(false, HostProtocol.Version, "", null);

    /** 对端是否声明了某项能力(见 HostCapabilities); 旧宿主不声明任何能力。 */
    public bool Supports(string capability) => Hello.Capabilities?.Contains(capability) == true;

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

    public async IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 重新订阅前停掉服务端旧泵, 并换成新通道: 否则旧泵与新泵会同时推送, 事件翻倍。
        // 老版本宿主没有 session.unsubscribe: 尽力而为, MethodNotFound 时跳过(不影响订阅)。
        try
        {
            await SendAsync(HostProtocol.MethodSessionUnsubscribe, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcException error) when (error.Code == JsonRpcPeer.MethodNotFound)
        {
        }
        var channel = Channel.CreateUnbounded<RemoteEventInfo>();
        _eventStreams[sessionId] = channel;
        await SendAsync(HostProtocol.MethodSessionSubscribe, Serialize(new RemoteSubscribeRequest(sessionId, fromSeq), HostProtocolJsonContext.Default.RemoteSubscribeRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public Task UnsubscribeAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionUnsubscribe, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionMessage, Serialize(new RemoteMessageRequest(sessionId, text, images), HostProtocolJsonContext.Default.RemoteMessageRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionInterrupt, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodToolsList, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.IReadOnlyListString, cancellationToken);

    public IAsyncEnumerable<RemoteApprovalRequest> ApprovalsAsync(CancellationToken cancellationToken = default)
        => _approvals.Reader.ReadAllAsync(cancellationToken);

    public Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodApprovalRespond, Serialize(new RemoteApprovalResponse(requestId, allow, reason), HostProtocolJsonContext.Default.RemoteApprovalResponse), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public IAsyncEnumerable<RemoteQuestionRequest> QuestionsAsync(CancellationToken cancellationToken = default)
        => _questions.Reader.ReadAllAsync(cancellationToken);

    public Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodQuestionRespond, Serialize(new RemoteQuestionResponse(requestId, answers), HostProtocolJsonContext.Default.RemoteQuestionResponse), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public IAsyncEnumerable<RemotePendingResolved> ResolutionsAsync(CancellationToken cancellationToken = default)
        => _resolutions.Reader.ReadAllAsync(cancellationToken);

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

    /** 每次 attach 换一条新通道: host 会从整屏快照重新开始推送, 旧通道里残留的字节不能混进来。 */
    public async Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<RemotePtyOutput>();
        _ptyStreams[ptyId] = channel;
        await SendAsync(HostProtocol.MethodPtyAttach, Serialize(new RemotePtyRef(ptyId), HostProtocolJsonContext.Default.RemotePtyRef), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
        // 远端 PTY 退出(Eof)时 attach 就该返回: 输入泵跟着停, 不等调用方关闭输入流。
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpInput = Task.Run(() => PumpPtyInputAsync(ptyId, input, inputLifetime.Token), cancellationToken);
        try
        {
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
            // 同一 PTY 已被本客户端重新 attach 时通道已换新, 不能 detach 掉新的那条推送。
            if (_ptyStreams.TryRemove(new KeyValuePair<string, Channel<RemotePtyOutput>>(ptyId, channel)))
                await DetachPtyAsync(ptyId).ConfigureAwait(false);
            await inputLifetime.CancelAsync().ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    /** attach 结束时让 host 停掉本连接的推送(PTY 本身继续运行); 旧宿主没有 detach, 连接断开不可用时同样忽略。 */
    private async Task DetachPtyAsync(string ptyId)
    {
        if (!Supports(HostCapabilities.PtyDaemon) || _peer.IsClosed)
            return;
        try
        {
            await SendAsync(HostProtocol.MethodPtyDetach, Serialize(new RemotePtyRef(ptyId), HostProtocolJsonContext.Default.RemotePtyRef), HostProtocolJsonContext.Default.Boolean, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or ObjectDisposedException)
        {
        }
    }

    private void OnNotification(JsonRpcMessage message)
    {
        switch (message.Method)
        {
            case HostProtocol.NotificationEvent when Parse(message, HostProtocolJsonContext.Default.RemoteEventInfo) is { } item
                && _eventStreams.TryGetValue(item.SessionId, out var events):
                events.Writer.TryWrite(item);
                break;
            case HostProtocol.NotificationApproval when Parse(message, HostProtocolJsonContext.Default.RemoteApprovalRequest) is { } request:
                _approvals.Writer.TryWrite(request);
                break;
            case HostProtocol.NotificationQuestion when Parse(message, HostProtocolJsonContext.Default.RemoteQuestionRequest) is { } question:
                _questions.Writer.TryWrite(question);
                break;
            case HostProtocol.NotificationPendingResolved when Parse(message, HostProtocolJsonContext.Default.RemotePendingResolved) is { } resolved:
                _resolutions.Writer.TryWrite(resolved);
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
            case HostProtocol.NotificationPtyOutput when Parse(message, HostProtocolJsonContext.Default.RemotePtyOutput) is { } output
                && _ptyStreams.TryGetValue(output.PtyId, out var pty):
                pty.Writer.TryWrite(output);
                break;
        }
    }

    private static T? Parse<T>(JsonRpcMessage message, JsonTypeInfo<T> info)
        => message.Params is { } element ? JsonSerializer.Deserialize(element, info) : default;

    private static JsonElement Serialize<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToElement(value, info);

    private async Task<T> SendAsync<T>(string method, JsonElement? parameters, JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var result = await _peer.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        if (result is not { } value)
            throw new IOException($"host returned no result for {method}");
        return JsonSerializer.Deserialize(value, info) ?? throw new IOException($"host returned an invalid result for {method}");
    }

    public ValueTask DisposeAsync() => _peer.DisposeAsync();
}
