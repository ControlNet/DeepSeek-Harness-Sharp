using System.Threading.Channels;
using Dsh.AgentAdapters;
using Dsh.Notifications;

namespace Dsh.RemoteHost;

/**
 * The machine's notification history (host.notify.*): the host is its only writer, every client reads it and shares one read state.
 * Sources: approvals and questions entering the pending queue, sessions exiting (with their exit code), and whatever the host reports
 * through Report (terminal agents waiting for input or approval, agent errors).
 * Appends go through one queue, so producers that run under other locks only hand the record off.
 */
public sealed class HostNotificationCenter : IAsyncDisposable
{
    public const string FileName = "notifications.jsonl";

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;

    private readonly NotificationHistory _history;
    private readonly HostSessionRegistry _sessions;
    private readonly HostPendingDecisions _pending;
    private readonly HostBroadcast _broadcast = new();
    private readonly Channel<NotificationRecord> _queue = Channel.CreateUnbounded<NotificationRecord>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _markGate = new(1, 1);
    private readonly Lock _statusGate = new();
    private readonly Dictionary<string, string> _lastStatus = new(StringComparer.Ordinal);
    private readonly Task _writer;

    private HostNotificationCenter(NotificationHistory history, string machineId, HostSessionRegistry sessions, HostPendingDecisions pending)
    {
        _history = history;
        MachineId = machineId;
        _sessions = sessions;
        _pending = pending;
        foreach (var session in sessions.List())
            _lastStatus[session.SessionId] = session.Status;
        sessions.Changed += OnSessionChanged;
        pending.Asked += OnAsked;
        _writer = Task.Run(WriteLoopAsync);
    }

    /** MachineId on every record: the host's machine name, so clients aggregating several hosts can tell them apart. */
    public string MachineId { get; }

    public static async Task<HostNotificationCenter> OpenAsync(string path, HostSessionRegistry sessions, HostPendingDecisions pending, CancellationToken cancellationToken)
    {
        var history = await NotificationHistory.OpenAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new HostNotificationCenter(history, Environment.MachineName, sessions, pending);
    }

    /** One per connection: live host.notify.added and host.notify.changed pushes; the history itself comes from host.notify.list. */
    public HostSubscription Subscribe() => _broadcast.Subscribe([]);

    public RemoteNotifyPage Query(RemoteNotifyQuery query)
    {
        var kinds = query.Kinds is { Count: > 0 } wanted ? wanted.ToHashSet() : null;
        var matching = _history.Query(new NotificationFilter(SessionId: query.SessionId, UnreadOnly: query.UnreadOnly))
            .Where(record => kinds?.Contains(record.Kind) != false)
            .ToList();
        var size = Math.Clamp(query.Limit ?? DefaultPageSize, 1, MaxPageSize);
        return new RemoteNotifyPage([.. matching.Skip(Math.Max(query.Offset, 0)).Take(size)], matching.Count, _history.CountUnread());
    }

    /** Marks notifications read and tells every client which ones changed; returns how many changed. */
    public async Task<int> MarkReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken)
    {
        await _markGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = request.All
                ? await _history.MarkAllReadAsync(new NotificationFilter(SessionId: request.SessionId), cancellationToken).ConfigureAwait(false)
                : await _history.MarkReadAsync(request.Ids ?? [], cancellationToken).ConfigureAwait(false);
            if (changed.Count > 0)
                _broadcast.Publish(HostNotification.Create(
                    HostProtocol.NotificationNotifyChanged, new RemoteNotifyChanged(changed, _history.CountUnread()), HostProtocolJsonContext.Default.RemoteNotifyChanged));
            return changed.Count;
        }
        finally
        {
            _markGate.Release();
        }
    }

    /** Queues a notification; Id defaults to a fresh one (pass a stable id to make repeats of the same event collapse). */
    public void Report(NotificationKind kind, string? sessionId, string title, string? body = null, NotificationTarget? target = null, string? id = null)
        => _queue.Writer.TryWrite(new NotificationRecord(
            id ?? Guid.NewGuid().ToString("N"),
            MachineId,
            sessionId,
            kind,
            title,
            body,
            DateTimeOffset.UtcNow,
            target ?? new NotificationTarget(NotificationView.Session)));

    /** A terminal-mode agent's screen turned to an input prompt or an approval dialog; busy and unknown screens are not reported. */
    public void ReportScreen(string sessionId, AgentScreenState state)
    {
        if (state == AgentScreenState.WaitingForApproval)
            Report(NotificationKind.ApprovalRequest, sessionId, $"{Label(sessionId)} needs approval", "the agent shows an approval prompt in its terminal");
        else if (state == AgentScreenState.WaitingForInput)
            Report(NotificationKind.WaitingForInput, sessionId, $"{Label(sessionId)} is waiting for input");
    }

    /** A display name for a session in notification titles. */
    public string Label(string sessionId)
        => _sessions.Get(sessionId) is { } session ? session.Title ?? session.AgentKind ?? session.SessionId : sessionId;

    private void OnAsked(HostPendingAsked asked)
    {
        var (kind, verb) = asked.Kind == PendingKinds.Approval
            ? (NotificationKind.ApprovalRequest, "needs approval")
            : (NotificationKind.Question, "asks a question");
        Report(kind, asked.SessionId, $"{Label(asked.SessionId)} {verb}", asked.Summary, new NotificationTarget(NotificationView.PendingRequest, asked.RequestId), asked.RequestId);
    }

    /** Runs under the registry lock: only compares and queues. */
    private void OnSessionChanged(RemoteHostSession session)
    {
        string? previous;
        lock (_statusGate)
        {
            _lastStatus.TryGetValue(session.SessionId, out previous);
            _lastStatus[session.SessionId] = session.Status;
        }
        if (session.Status != HostSessionStatuses.Exited || previous is null or HostSessionStatuses.Exited)
            return;
        var name = session.Title ?? session.AgentKind ?? session.SessionId;
        Report(NotificationKind.SessionExited, session.SessionId, $"{name} exited", session.ExitCode is { } code ? $"exit code {code}" : null);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var record in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (await _history.AppendAsync(record).ConfigureAwait(false))
                _broadcast.Publish(HostNotification.Create(HostProtocol.NotificationNotifyAdded, record, HostProtocolJsonContext.Default.NotificationRecord));
        }
    }

    /** Stops listening first, so sessions ended by the host's own shutdown do not land in the history. */
    public async ValueTask DisposeAsync()
    {
        _sessions.Changed -= OnSessionChanged;
        _pending.Asked -= OnAsked;
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        _markGate.Dispose();
    }
}
