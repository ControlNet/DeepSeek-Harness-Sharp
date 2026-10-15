using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Fleet;

/**
 * The single connection a client keeps to one machine; every session on that machine shares it.
 * A background loop connects (deploying first when needed), keeps the link alive with a heartbeat, and reconnects with exponential backoff after a drop.
 * After every (re)connection it re-reads host info, the session registry and the pending requests, and resumes event subscriptions from the last seen seq.
 * Sessions and pending requests are the last known values while the machine is not connected.
 */
public sealed class MachineConnection : IAsyncDisposable
{
    /** How many resolved request ids are remembered, so that a request re-sent before its resolution was processed does not come back. */
    private const int ResolvedMemory = 1024;

    private readonly FleetOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RemoteHostSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteApprovalRequest> _approvals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteQuestionRequest> _questions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);
    private readonly Queue<string> _resolvedOrder = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<HostLease> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _commandPath;
    private Task? _loop;

    public MachineConnection(MachineSpec machine, FleetOptions options)
    {
        Machine = machine;
        _options = options;
    }

    public MachineSpec Machine { get; }

    public MachineConnectionState State { get; private set; } = MachineConnectionState.Offline;

    /** The last failure, or the deployment step in progress. */
    public string? Detail { get; private set; }

    public HostInfo? Info { get; private set; }

    /** The ssh process of the current link (SSH machines only). */
    internal Process? LinkProcess { get; private set; }

    public event Action<MachineConnection>? StateChanged;

    public event Action<MachineConnection>? SessionsChanged;

    public event Action<MachineConnection>? PendingChanged;

    public IReadOnlyList<RemoteHostSession> Sessions
    {
        get
        {
            lock (_gate)
                return [.. _sessions.Values];
        }
    }

    public RemotePendingList Pending
    {
        get
        {
            lock (_gate)
                return new RemotePendingList([.. _approvals.Values], [.. _questions.Values]);
        }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_lifetime.Token));

    /** Completes once the first attempt has either connected or failed. */
    public Task WaitSettledAsync(CancellationToken cancellationToken) => _settled.Task.WaitAsync(cancellationToken);

    public Task<HostLease> WaitConnectedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return _connected.Task.WaitAsync(cancellationToken);
    }

    /**
     * Session events that survive reconnection: after a drop the subscription resumes at the seq after the last delivered event.
     * When the host cannot resume from that seq, it subscribes from the start and skips what was already delivered.
     * The stream fails when the host rejects the subscription itself (for example an unknown session).
     */
    public IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<RemoteEventInfo>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _ = FollowEventsAsync(sessionId, new EventCursor(fromSeq), channel.Writer, cancellationToken);
        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    public async Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason, CancellationToken cancellationToken)
    {
        var lease = await WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await lease.Client.RespondApprovalAsync(requestId, allow, reason, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken)
    {
        var lease = await WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await lease.Client.RespondQuestionAsync(requestId, answers, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_loop is { } loop)
            await loop.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var everConnected = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            // A reconnect keeps showing why the previous link dropped until the new attempt settles.
            SetState(everConnected ? MachineConnectionState.Reconnecting : MachineConnectionState.Connecting, everConnected ? Detail : null);
            var link = await TryOpenAsync(cancellationToken).ConfigureAwait(false);
            if (link is null)
                failures++;
            else
            {
                (failures, everConnected) = (0, true);
                await ServeAsync(link, cancellationToken).ConfigureAwait(false);
            }

            _settled.TrySetResult();
            if (!_options.AutoReconnect || !await DelayAsync(_options.Backoff(failures), cancellationToken).ConfigureAwait(false))
                break;
        }

        SetState(MachineConnectionState.Offline, Detail);
    }

    private async Task<HostLink?> TryOpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Machine.IsLocal)
                return await HostLink.OpenLocalAsync(Machine, _options, cancellationToken).ConfigureAwait(false);
            var commandPath = await ResolveCommandPathAsync(cancellationToken).ConfigureAwait(false);
            return await HostLink.OpenSshAsync(Machine, commandPath, _options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // The deployed path may be stale (removed or replaced); resolve it again on the next attempt.
            _commandPath = null;
            SetState(MachineConnectionState.Offline, error.Message);
            return null;
        }
    }

    /** The remote dsharp to run: deploy or upgrade it when automatic deployment is on, otherwise use whatever the probe finds (null falls back to PATH or the remote directory). */
    private async Task<string?> ResolveCommandPathAsync(CancellationToken cancellationToken)
    {
        if (_commandPath is not null)
            return _commandPath;
        if (_options.Deploy.AutoDeploy)
        {
            SetState(MachineConnectionState.Deploying, null);
            var deployed = await RemoteDsharpInstaller.EnsureAsync(
                Machine, _options.Deploy, step => SetState(MachineConnectionState.Deploying, step), cancellationToken, _options.ArchiveFactory).ConfigureAwait(false);
            return _commandPath = deployed.CommandPath;
        }

        try
        {
            return _commandPath = (await RemoteDsharpInstaller.InspectAsync(Machine, cancellationToken).ConfigureAwait(false)).CommandPath;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /** Serves one established link until it drops: refresh, publish the lease, pump pushes, and watch the heartbeat. */
    private async Task ServeAsync(HostLink link, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] pumps = [];
        try
        {
            pumps = StartPumps(link.Client, lifetime.Token);
            await RefreshAsync(link.Client, lifetime.Token).ConfigureAwait(false);
            Publish(new HostLease(link.Client, lifetime.Token), link.Process);
            await WatchAsync(link, lifetime.Token).ConfigureAwait(false);
            Detail = "connection lost";
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            Detail = error.Message;
        }
        finally
        {
            Unpublish();
            await lifetime.CancelAsync().ConfigureAwait(false);
            await link.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
    }

    private Task[] StartPumps(RemoteHostClient client, CancellationToken cancellationToken) =>
    [
        PumpAsync(client.ApprovalsAsync(cancellationToken), AddApproval),
        PumpAsync(client.QuestionsAsync(cancellationToken), AddQuestion),
        PumpAsync(client.ResolutionsAsync(cancellationToken), Resolve),
        PumpAsync(client.HostSessionChangesAsync(cancellationToken), ApplySession),
    ];

    private static async Task PumpAsync<T>(IAsyncEnumerable<T> source, Action<T> apply)
    {
        try
        {
            await foreach (var item in source.ConfigureAwait(false))
                apply(item);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /** Re-reads everything a reconnect may have missed; the pushes already being pumped are newer and keep applying after it. */
    private async Task RefreshAsync(RemoteHostClient client, CancellationToken cancellationToken)
    {
        Info = await client.InfoAsync(cancellationToken).ConfigureAwait(false);
        var sessions = client.Supports(HostCapabilities.SessionRegistry)
            ? await client.ListHostSessionsAsync(cancellationToken).ConfigureAwait(false)
            : [.. (await client.ListSessionsAsync(cancellationToken).ConfigureAwait(false)).Select(FromHarnessSession)];
        lock (_gate)
        {
            _sessions.Clear();
            foreach (var session in sessions)
                _sessions[session.SessionId] = session;
        }

        SessionsChanged?.Invoke(this);
        if (client.Supports(HostCapabilities.PendingBroadcast))
            ReplacePending(await client.ListPendingAsync(cancellationToken).ConfigureAwait(false));
    }

    /** Hosts without the session registry only list harness sessions. */
    private static RemoteHostSession FromHarnessSession(RemoteSessionInfo session)
        => new(session.Id, HostSessionKinds.Harness, HostAgentKinds.Dsharp, session.Cwd, session.Title, session.Status, session.StartedAt, session.UpdatedAt, 0, null);

    /** Returns when the ssh process exits, a heartbeat ping fails, or the client is shutting down. */
    private async Task WatchAsync(HostLink link, CancellationToken cancellationToken)
    {
        var exited = link.Exited;
        while (!cancellationToken.IsCancellationRequested)
        {
            var tick = Task.Delay(_options.HeartbeatInterval, cancellationToken);
            if (await Task.WhenAny(tick, exited).ConfigureAwait(false) == exited || tick.IsCanceled)
                return;
            if (!await PingAsync(link.Client, cancellationToken).ConfigureAwait(false))
                return;
        }
    }

    private async Task<bool> PingAsync(RemoteHostClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HeartbeatTimeout);
        try
        {
            await client.PingAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or JsonRpcException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void Publish(HostLease lease, Process? process)
    {
        lock (_gate)
            _connected.TrySetResult(lease);
        LinkProcess = process;
        SetState(MachineConnectionState.Connected, null);
        _settled.TrySetResult();
    }

    private void Unpublish()
    {
        lock (_gate)
        {
            if (_connected.Task.IsCompleted)
                _connected = new TaskCompletionSource<HostLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        LinkProcess = null;
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SetState(MachineConnectionState state, string? detail)
    {
        State = state;
        Detail = detail;
        StateChanged?.Invoke(this);
    }

    private void ApplySession(RemoteHostSession session)
    {
        lock (_gate)
            _sessions[session.SessionId] = session;
        SessionsChanged?.Invoke(this);
    }

    private void AddApproval(RemoteApprovalRequest request)
    {
        lock (_gate)
        {
            if (!_resolved.Contains(request.RequestId))
                _approvals[request.RequestId] = request;
        }

        PendingChanged?.Invoke(this);
    }

    private void AddQuestion(RemoteQuestionRequest request)
    {
        lock (_gate)
        {
            if (!_resolved.Contains(request.RequestId))
                _questions[request.RequestId] = request;
        }

        PendingChanged?.Invoke(this);
    }

    private void Resolve(RemotePendingResolved resolved)
    {
        lock (_gate)
        {
            _approvals.Remove(resolved.RequestId);
            _questions.Remove(resolved.RequestId);
            RememberResolved(resolved.RequestId);
        }

        PendingChanged?.Invoke(this);
    }

    private void RememberResolved(string requestId)
    {
        if (!_resolved.Add(requestId))
            return;
        _resolvedOrder.Enqueue(requestId);
        if (_resolvedOrder.Count > ResolvedMemory)
            _resolved.Remove(_resolvedOrder.Dequeue());
    }

    private void ReplacePending(RemotePendingList pending)
    {
        lock (_gate)
        {
            _approvals.Clear();
            _questions.Clear();
            foreach (var approval in pending.Approvals.Where(item => !_resolved.Contains(item.RequestId)))
                _approvals[approval.RequestId] = approval;
            foreach (var question in pending.Questions.Where(item => !_resolved.Contains(item.RequestId)))
                _questions[question.RequestId] = question;
        }

        PendingChanged?.Invoke(this);
    }

    private async Task FollowEventsAsync(string sessionId, EventCursor cursor, ChannelWriter<RemoteEventInfo> writer, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var lease = await WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
                await PumpEventsAsync(lease, sessionId, cursor, writer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (JsonRpcException error)
        {
            failure = error;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    /** Pumps one link's subscription; returns when the link drops. A rejected resume from the middle is retried from the start once. */
    private static async Task PumpEventsAsync(HostLease lease, string sessionId, EventCursor cursor, ChannelWriter<RemoteEventInfo> writer, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        try
        {
            await foreach (var item in lease.Client.SubscribeAsync(sessionId, cursor.SubscribeFrom, linked.Token).ConfigureAwait(false))
            {
                if (cursor.Accept(item))
                    await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        catch (JsonRpcException) when (cursor.SubscribeFrom > 0)
        {
            cursor.RestartFromBeginning();
        }
    }

    /** Where a resilient subscription stands: the next seq to deliver and the seq to subscribe from (0 after a rejected resume). */
    private sealed class EventCursor(long next)
    {
        private long _next = next;

        public long SubscribeFrom { get; private set; } = next;

        public void RestartFromBeginning() => SubscribeFrom = 0;

        /** Delivers each seq once: events below the next seq were already delivered before the reconnect. */
        public bool Accept(RemoteEventInfo item)
        {
            if (JsonSerializer.Deserialize(item.EventJson, FleetJsonContext.Default.EventEnvelope) is not { } envelope)
                return true;
            if (envelope.Seq < _next)
                return false;
            _next = envelope.Seq + 1;
            SubscribeFrom = _next;
            return true;
        }
    }
}
