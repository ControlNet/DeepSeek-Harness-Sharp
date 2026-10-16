using Dsh.Boot;
using Dsh.RemoteHost;

namespace Dsh.Fleet;

/** A session together with the machine it runs on. */
public sealed record FleetSession(string Machine, RemoteHostSession Session);

/** Sessions of one project directory on one machine; ProjectPath is null for sessions without a project. */
public sealed record FleetProjectGroup(string? ProjectPath, IReadOnlyList<RemoteHostSession> Sessions);

/** All sessions of one machine, grouped by project. */
public sealed record FleetMachineGroup(string Machine, IReadOnlyList<FleetProjectGroup> Projects);

/** An approval or question waiting for an answer: exactly one of Approval and Question is set. */
public sealed record FleetPendingItem(string Machine, string RequestId, string SessionId, string Kind, RemoteApprovalRequest? Approval, RemoteQuestionRequest? Question);

/** What a client shows for a machine: its connection state, the host's release and the last failure or deployment step. */
public sealed record FleetMachineStatus(string Machine, string Kind, string Target, MachineConnectionState State, string? HostVersion, string? Detail);

/**
 * The aggregated view of every machine, independent of any UI framework.
 * It owns one MachineConnection per machine and folds their sessions, pending requests and states into flat and grouped snapshots.
 * Changed is raised from background threads whenever any machine's state, sessions or pending requests change; UIs marshal it themselves and re-read the snapshots.
 */
public sealed class FleetClient : IAsyncDisposable
{
    private readonly List<MachineConnection> _machines;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    public FleetClient(IEnumerable<MachineSpec> machines, FleetOptions options)
    {
        _machines = [.. machines.Select(machine => new MachineConnection(machine, options))];
        foreach (var machine in _machines)
        {
            machine.StateChanged += OnMachineChanged;
            machine.SessionsChanged += OnMachineChanged;
            machine.PendingChanged += OnMachineChanged;
        }
    }

    /** A client for the local machine and every machine in the store of this harness home, deploying with the stored preferences. */
    public static FleetClient FromStore(HarnessHome home, FleetOptions options)
    {
        var config = new MachineStore(home).Load();
        return new FleetClient([MachineSpec.Local(home.Root), .. config.Machines], options with { Deploy = config.Deploy });
    }

    public event Action? Changed;
    internal event Action<FleetClient>? Disposed;

    public IReadOnlyList<MachineConnection> Machines => _machines;

    public MachineConnection? Find(string name) => _machines.FirstOrDefault(machine => string.Equals(machine.Machine.Name, name, StringComparison.Ordinal));

    public void Start()
    {
        foreach (var machine in _machines)
            machine.Start();
    }

    /** Completes once every machine has either connected or failed its first attempt. */
    public Task WaitSettledAsync(CancellationToken cancellationToken)
        => Task.WhenAll(_machines.Select(machine => machine.WaitSettledAsync(cancellationToken)));

    public IReadOnlyList<FleetMachineStatus> Statuses()
        => [.. _machines.Select(machine => new FleetMachineStatus(
            machine.Machine.Name, machine.Machine.Kind, machine.Machine.Target, machine.State, machine.Info?.HostVersion, machine.Detail))];

    /** Every session, ordered by machine (in configuration order), then project, then creation time. */
    public IReadOnlyList<FleetSession> Sessions()
        => [.. _machines.SelectMany(machine => machine.Sessions
            .OrderBy(session => session.ProjectPath ?? "", StringComparer.Ordinal)
            .ThenBy(session => session.CreatedAt)
            .Select(session => new FleetSession(machine.Machine.Name, session)))];

    /** Machine → project → sessions, the dashboard's grouping. */
    public IReadOnlyList<FleetMachineGroup> Groups()
        => [.. _machines.Select(machine => new FleetMachineGroup(
            machine.Machine.Name,
            [.. machine.Sessions
                .GroupBy(session => session.ProjectPath)
                .OrderBy(group => group.Key ?? "", StringComparer.Ordinal)
                .Select(group => new FleetProjectGroup(group.Key, [.. group.OrderBy(session => session.CreatedAt)]))]))];

    public IReadOnlyList<FleetPendingItem> Pending()
        => [.. _machines.SelectMany(machine => PendingOf(machine.Machine.Name, machine.Pending))];

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
            return new ValueTask(_disposeTask ??= DisposeMachinesAsync());
    }

    private async Task DisposeMachinesAsync()
    {
        List<Exception>? errors = null;
        foreach (var machine in _machines)
        {
            machine.StateChanged -= OnMachineChanged;
            machine.SessionsChanged -= OnMachineChanged;
            machine.PendingChanged -= OnMachineChanged;
            try
            {
                await machine.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
        Disposed?.Invoke(this);
        if (errors is not null)
            throw new AggregateException(errors);
    }

    private static IEnumerable<FleetPendingItem> PendingOf(string machine, RemotePendingList pending)
        => pending.Approvals
            .Select(approval => new FleetPendingItem(machine, approval.RequestId, approval.SessionId, PendingKinds.Approval, approval, null))
            .Concat(pending.Questions.Select(question => new FleetPendingItem(machine, question.RequestId, question.SessionId, PendingKinds.Question, null, question)));

    private void OnMachineChanged(MachineConnection machine) => Changed?.Invoke();
}
