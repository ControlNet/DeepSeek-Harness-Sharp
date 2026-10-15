namespace Dsh.RemoteHost;

/** 谁给出了结局: 客户端自报名(或连接 id)与连接 id; host 自己取消时没有连接。 */
public readonly record struct PendingResolver(string Name, string? ConnectionId)
{
    public static readonly PendingResolver Host = new("host", null);
}

/**
 * 远端发起、等待用户决策的审批与问答: 广播给所有连接, 先答者生效, 随后向所有连接推送 host.pending.resolved。
 * 请求一直保留到有人回答, 或所属会话被中断/结束(取消); 新连接订阅时先补发全部未决请求。
 * 与具体后端无关: harness 的审批瀑布与后续的外部 Agent 权限请求都经它进入同一个待决策队列。
 */
public sealed class HostPendingDecisions : IDisposable
{
    private readonly Lock _gate = new();
    private readonly HostBroadcast _broadcast = new();
    private readonly OrderedDictionary<string, PendingEntry> _entries = new(StringComparer.Ordinal);

    /** 订阅了未决请求推送的连接数。 */
    public int SubscriberCount => _broadcast.SubscriberCount;

    /** 某会话的未决数量变化(参数为会话 id); 在锁外触发。 */
    public event Action<string>? CountChanged;

    private sealed record PendingEntry(
        string Kind,
        string SessionId,
        string? Scope,
        HostNotification Asked,
        RemoteApprovalRequest? Approval,
        RemoteQuestionRequest? Question,
        TaskCompletionSource<object?> Completion);

    /** 挂起一条审批直到有人回答; 被取消时返回 null。Scope 是发起方的细分标识(如子代理 id), 供 CancelScope 精确取消。 */
    public Task<RemoteApprovalResponse?> AskApprovalAsync(RemoteApprovalRequest request, string? scope = null)
        => AskAsync<RemoteApprovalResponse>(new PendingEntry(
            PendingKinds.Approval,
            request.SessionId,
            scope,
            HostNotification.Create(HostProtocol.NotificationApproval, request, HostProtocolJsonContext.Default.RemoteApprovalRequest),
            request,
            null,
            NewCompletion()), request.RequestId);

    /** 挂起一条问答直到有人回答; 被取消时返回 null。 */
    public Task<RemoteQuestionResponse?> AskQuestionAsync(RemoteQuestionRequest request, string? scope = null)
        => AskAsync<RemoteQuestionResponse>(new PendingEntry(
            PendingKinds.Question,
            request.SessionId,
            scope,
            HostNotification.Create(HostProtocol.NotificationQuestion, request, HostProtocolJsonContext.Default.RemoteQuestionRequest),
            null,
            request,
            NewCompletion()), request.RequestId);

    /** 回答审批; 返回 false 表示该请求已不在(别人先答了或已取消)。 */
    public bool RespondApproval(RemoteApprovalResponse response, PendingResolver resolver)
        => Resolve(response.RequestId, PendingKinds.Approval, response, response.Allow ? PendingResolutions.Allowed : PendingResolutions.Rejected, resolver);

    /** 回答问答; 返回 false 表示该请求已不在。 */
    public bool RespondQuestion(RemoteQuestionResponse response, PendingResolver resolver)
        => Resolve(response.RequestId, PendingKinds.Question, response, PendingResolutions.Answered, resolver);

    /** 会话被中断或结束: 取消它的全部未决请求。 */
    public void CancelSession(string sessionId) => CancelWhere(entry => entry.SessionId == sessionId);

    /** 发起方(Scope)已不再等待: 只取消它自己发起的请求。 */
    public void CancelScope(string scope) => CancelWhere(entry => entry.Scope == scope);

    public RemotePendingList List()
    {
        lock (_gate)
        {
            return new RemotePendingList(
                [.. _entries.Values.Select(entry => entry.Approval).OfType<RemoteApprovalRequest>()],
                [.. _entries.Values.Select(entry => entry.Question).OfType<RemoteQuestionRequest>()]);
        }
    }

    public int CountFor(string sessionId)
    {
        lock (_gate)
            return _entries.Values.Count(entry => entry.SessionId == sessionId);
    }

    /** 每个连接一份: 先补发当前全部未决请求, 再接实时的请求与结局推送。 */
    public HostSubscription Subscribe()
    {
        lock (_gate)
            return _broadcast.Subscribe([.. _entries.Values.Select(entry => entry.Asked)]);
    }

    public void Dispose() => CancelWhere(_ => true);

    private static TaskCompletionSource<object?> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<T?> AskAsync<T>(PendingEntry entry, string requestId) where T : class
    {
        lock (_gate)
        {
            if (!_entries.TryAdd(requestId, entry))
                throw new InvalidOperationException($"pending request id already in use: {requestId}");
            _broadcast.Publish(entry.Asked);
        }
        CountChanged?.Invoke(entry.SessionId);
        return await entry.Completion.Task.ConfigureAwait(false) as T;
    }

    private bool Resolve(string requestId, string kind, object answer, string resolution, PendingResolver resolver)
    {
        PendingEntry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(requestId, out entry) || entry.Kind != kind)
                return false;
            _entries.Remove(requestId);
            _broadcast.Publish(Resolved(requestId, entry, resolution, resolver));
        }
        entry.Completion.TrySetResult(answer);
        CountChanged?.Invoke(entry.SessionId);
        return true;
    }

    private void CancelWhere(Func<PendingEntry, bool> match)
    {
        List<PendingEntry> cancelled;
        lock (_gate)
        {
            var ids = _entries.Where(pair => match(pair.Value)).Select(pair => pair.Key).ToList();
            cancelled = [.. ids.Select(id => _entries[id])];
            foreach (var id in ids)
            {
                _broadcast.Publish(Resolved(id, _entries[id], PendingResolutions.Cancelled, PendingResolver.Host));
                _entries.Remove(id);
            }
        }
        foreach (var entry in cancelled)
            entry.Completion.TrySetResult(null);
        foreach (var sessionId in cancelled.Select(entry => entry.SessionId).Distinct(StringComparer.Ordinal))
            CountChanged?.Invoke(sessionId);
    }

    private static HostNotification Resolved(string requestId, PendingEntry entry, string resolution, PendingResolver resolver)
        => HostNotification.Create(
            HostProtocol.NotificationPendingResolved,
            new RemotePendingResolved(requestId, entry.Kind, entry.SessionId, resolution, resolver.Name, resolver.ConnectionId),
            HostProtocolJsonContext.Default.RemotePendingResolved);
}
