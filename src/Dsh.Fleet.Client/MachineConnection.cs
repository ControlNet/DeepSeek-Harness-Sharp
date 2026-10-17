using System.Diagnostics;
using Dsh.Notifications;
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
    private readonly FleetOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RemoteHostSession> _sessions = new(StringComparer.Ordinal);
    private RemotePendingList _pending = new([], []);
    private readonly RemoteHostSubscriptions _subscriptions;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<HostLease> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _commandPath;
    private Task? _loop;

    public MachineConnection(MachineSpec machine, FleetOptions options)
    {
        Machine = machine;
        _options = options;
        _subscriptions = new RemoteHostSubscriptions(WaitConnectedAsync, _lifetime.Token);
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

    /** A notification the host added to its history (host.notify.added). */
    public event Action<MachineConnection, NotificationRecord>? NotificationAdded;

    /** Notifications some client marked read (host.notify.changed), with the ids that became read. */
    public event Action<MachineConnection, IReadOnlyList<string>>? NotificationsRead;

    /** The history was re-read after a (re)connection; records added while the machine was unreachable are only visible through host.notify.list. */
    public event Action<MachineConnection>? NotificationsReloaded;

    /** Unread notifications in the host's history; 0 while never connected or when the host keeps no history. */
    public int UnreadNotifications { get; private set; }

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
                return _pending;
        }
    }

    public void Start() => _loop ??= RunAsync(_lifetime.Token);

    /** Completes once the first attempt has either connected or failed. */
    public Task WaitSettledAsync(CancellationToken cancellationToken) => _settled.Task.WaitAsync(cancellationToken);

    public Task<HostLease> WaitConnectedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return _connected.Task.WaitAsync(cancellationToken);
    }

    /** 每个读者使用共享 RemoteHost 续订语义，不保存另一份历史或去重记忆。 */
    public IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
        => _subscriptions.SubscribeAsync(sessionId, fromSeq, cancellationToken);


    /**
     * Attaches to a PTY across reconnections: after a drop the next link attaches again and the host starts over with a full-screen snapshot.
     * Returns when the PTY ends; fails when the host does not know the PTY (for example it exited while the machine was unreachable).
     */
    public Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken,
        Func<RemoteHostClient, CancellationToken, Task>? prepare = null)
        => _subscriptions.AttachPtyAsync(ptyId, input, output, cancellationToken, prepare);

    /** Records a session this client just started or resumed, ahead of the registry push that announces it (the push, being newer, replaces it). */
    public void Remember(RemoteHostSession session) => ApplySession(session);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop is { } loop)
                await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Disposing while an attempt is still connecting cancels it; that ended FleetClient.DisposeAsync before the other machines were released.
        }

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
        // A cancelled attempt can also fail with its own error: an ssh killed by the cancellation exits with 255, which surfaced from DisposeAsync.
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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
                Machine, _options.Deploy, step => SetState(MachineConnectionState.Deploying, step), cancellationToken, _options.ArchiveFactory,
                localProxy: _options.LocalProxy).ConfigureAwait(false);
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
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] pumps = [];
        try
        {
            pumps = StartPumps(link.Client, lifetime.Token);
            await RefreshAsync(link.Client, lifetime.Token).ConfigureAwait(false);
            Publish(new HostLease(link.Client, lifetime.Token, closed.Task), link.Process);
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
            closed.TrySetResult();
            await link.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
    }

    private Task[] StartPumps(RemoteHostClient client, CancellationToken cancellationToken)
    {
        Task[] pumps =
        [
            PumpAsync(client.PendingChangesAsync(cancellationToken), change => ReplacePending(change.Pending)),
            PumpAsync(client.HostSessionChangesAsync(cancellationToken), ApplySession),
        ];
        if (!client.Supports(NotificationsProtocol.Capability))
            return pumps;
        return
        [
            .. pumps,
            PumpAsync(client.NotificationsAddedAsync(cancellationToken), AddNotification),
            PumpAsync(client.NotificationChangesAsync(cancellationToken), ApplyNotificationsRead),
        ];
    }

    private static async Task PumpAsync<T>(IAsyncEnumerable<T> source, Action<T> apply)
    {
        try
        {
            await foreach (var item in source.ConfigureAwait(false))
                apply(item);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    /** Re-reads everything a reconnect may have missed; the pushes already being pumped are newer and keep applying after it. */
    private async Task RefreshAsync(RemoteHostClient client, CancellationToken cancellationToken)
    {
        Info = await client.InfoAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await client.ListHostSessionsAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _sessions.Clear();
            foreach (var session in sessions)
                _sessions[session.SessionId] = session;
        }

        SessionsChanged?.Invoke(this);
        if (!client.Supports(NotificationsProtocol.Capability))
            return;
        var history = await client.ListNotificationsAsync(new RemoteNotifyQuery(Limit: 1), cancellationToken).ConfigureAwait(false);
        lock (_gate)
            UnreadNotifications = history.UnreadCount;
        NotificationsReloaded?.Invoke(this);
    }

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
        {
            if (session.Deleted)
                _sessions.Remove(session.SessionId);
            else
                _sessions[session.SessionId] = session;
        }
        SessionsChanged?.Invoke(this);
    }

    private void AddNotification(NotificationRecord record)
    {
        if (!record.IsRead)
        {
            lock (_gate)
                UnreadNotifications++;
        }

        NotificationAdded?.Invoke(this, record);
    }

    private void ApplyNotificationsRead(RemoteNotifyChanged changed)
    {
        lock (_gate)
            UnreadNotifications = changed.UnreadCount;
        NotificationsRead?.Invoke(this, changed.ReadIds);
    }

    private void ReplacePending(RemotePendingList pending)
    {
        lock (_gate)
            _pending = pending;
        PendingChanged?.Invoke(this);
    }
}
