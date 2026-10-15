using Dsh.Core;
using Dsh.Fleet;
using Dsh.Interaction;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * Dsh.Fleet against real hosts: two independent harness homes, each with its own host (real harness backend + loopback listener), act as two machines.
 * Covers the aggregated session and pending views, answering through the fleet, reconnecting after a host restart, and resuming a session subscription from the last seq.
 */
public sealed class FleetConnectionTests
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /**
     * Fast heartbeat and backoff so that drops are noticed and repaired within the test's time budget.
     * The ping timeout stays at the default: a stopped host closes the connection and fails the next ping at once, while a short timeout drops healthy links on a loaded machine.
     */
    internal static readonly FleetOptions TestOptions = new()
    {
        ClientName = "fleet-test",
        HeartbeatInterval = TimeSpan.FromMilliseconds(200),
        InitialBackoff = TimeSpan.FromMilliseconds(100),
        MaxBackoff = TimeSpan.FromSeconds(1),
    };

    [Fact(Timeout = 90000)]
    public async Task TwoMachines_AggregateSessionsAndPending_AndRecoverAfterHostRestart()
    {
        await using var alpha = await HarnessHostFixture.CreateAsync("fa", TestContext.Current.CancellationToken);
        await using var beta = await HarnessHostFixture.CreateAsync("fb", Ct);
        await using var fleet = new FleetClient([MachineSpec.Local(alpha.Root, "alpha"), MachineSpec.Local(beta.Root, "beta")], TestOptions);
        fleet.Start();
        await fleet.WaitSettledAsync(Ct);
        // A first attempt can fail on a loaded machine; the connection retries on its own.
        foreach (var machine in fleet.Machines)
            await machine.WaitConnectedAsync(Ct);
        Assert.All(fleet.Statuses(), status => Assert.Equal(MachineConnectionState.Connected, status.State));

        var alphaProject = Directory.CreateDirectory(Path.Combine(alpha.Root, "project-a")).FullName;
        var betaProject = Directory.CreateDirectory(Path.Combine(beta.Root, "project-b")).FullName;
        var alphaSession = await CreateSessionAsync(fleet, "alpha", alphaProject);
        var betaSession = await CreateSessionAsync(fleet, "beta", betaProject);
        await WaitForAsync(() => fleet.Sessions().Count(session => session.Session.SessionId == alphaSession || session.Session.SessionId == betaSession) == 2);

        var groups = fleet.Groups();
        Assert.Equal(["alpha", "beta"], groups.Select(group => group.Machine));
        Assert.Contains(groups[0].Projects, project => project.ProjectPath == alphaProject && project.Sessions.Any(session => session.SessionId == alphaSession));
        Assert.Contains(groups[1].Projects, project => project.ProjectPath == betaProject && project.Sessions.Any(session => session.SessionId == betaSession));
        Assert.All(fleet.Statuses(), status => Assert.Equal(RemoteHostServer.ReleaseVersion, status.HostVersion));

        var decision = RemoteHostMultiClientTests.RaiseApprovalAsync(beta, beta.Environment.Agent, "bash");
        await WaitForAsync(() => fleet.Pending().Any(item => item is { Machine: "beta", Kind: PendingKinds.Approval }));
        var pending = Assert.Single(fleet.Pending());
        Assert.Equal("bash", pending.Approval!.Tool);
        Assert.True(await (await fleet.Find("beta")!.WaitConnectedAsync(Ct)).Client.RespondApprovalAsync(pending.RequestId, true, null, Ct));
        Assert.Equal(ApprovalOutcome.AllowedOnce, await decision.WaitAsync(Arrival, Ct));
        await WaitForAsync(() => fleet.Pending().Count == 0);

        var states = RecordStates(fleet.Find("alpha")!);
        await alpha.RestartHostAsync(Ct);
        await WaitForAsync(() => states.Contains(MachineConnectionState.Reconnecting) && fleet.Find("alpha")!.State == MachineConnectionState.Connected);
        var restartedApproval = RemoteHostMultiClientTests.RaiseApprovalAsync(alpha, alpha.Environment.Agent, "write");
        await WaitForAsync(() => fleet.Pending().Any(item => item is { Machine: "alpha", Kind: PendingKinds.Approval }));
        Assert.Contains(fleet.Sessions(), session => session.Machine == "alpha" && session.Session.SessionId == alphaSession);
        Assert.Equal(MachineConnectionState.Connected, fleet.Find("beta")!.State);
        Assert.True(await (await fleet.Find("alpha")!.WaitConnectedAsync(Ct)).Client.RespondApprovalAsync(Assert.Single(fleet.Pending()).RequestId, false, null, Ct));
        Assert.Equal(ApprovalOutcome.Rejected, await restartedApproval.WaitAsync(Arrival, Ct));
    }

    /** The host goes away while events are appended; after the reconnect the subscription delivers exactly the missed events, each once. */
    [Fact(Timeout = 90000)]
    public async Task Subscription_ResumesFromTheLastSeq_AfterReconnect()
    {
        await using var host = await HarnessHostFixture.CreateAsync("fs", TestContext.Current.CancellationToken);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root, "solo"), TestOptions);
        connection.Start();
        await connection.WaitConnectedAsync(Ct);
        var session = host.Environment.Agent.Session;
        var delivered = new List<long>();
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reading = ReadSeqsAsync(connection.SubscribeAsync(session.Id.Value, 0, subscription.Token), delivered);

        await WaitForAsync(() => Snapshot(delivered).Count == session.Seq);
        var states = RecordStates(connection);
        await host.StopHostAsync();
        await WaitForAsync(() => connection.State != MachineConnectionState.Connected);
        var missedFrom = session.Seq;
        session.Append(new TurnStartPayload(1));
        session.Append(new TurnStartPayload(2));
        await host.StartHostAsync(Ct);

        await WaitForAsync(() => Snapshot(delivered).Count == session.Seq);
        Assert.Contains(MachineConnectionState.Reconnecting, states);
        Assert.Equal(Enumerable.Range(0, (int)session.Seq).Select(seq => (long)seq), Snapshot(delivered));
        Assert.Equal([missedFrom, missedFrom + 1], Snapshot(delivered).Skip((int)missedFrom));
        await subscription.CancelAsync();
        await reading;
    }

    private static async Task<string> CreateSessionAsync(FleetClient fleet, string machine, string cwd)
    {
        var lease = await fleet.Find(machine)!.WaitConnectedAsync(Ct);
        var created = await lease.Client.CreateSessionAsync(cwd, RemoteHostMultiClientTests.Selection, RemoteHostMultiClientTests.Spec, Ct);
        return created.Id;
    }

    internal static List<MachineConnectionState> RecordStates(MachineConnection connection)
    {
        var states = new List<MachineConnectionState>();
        connection.StateChanged += changed =>
        {
            lock (states)
                states.Add(changed.State);
        };
        return states;
    }

    private static async Task ReadSeqsAsync(IAsyncEnumerable<RemoteEventInfo> events, List<long> delivered)
    {
        try
        {
            await foreach (var item in events)
            {
                using var document = System.Text.Json.JsonDocument.Parse(item.EventJson);
                lock (delivered)
                    delivered.Add(document.RootElement.GetProperty("seq").GetInt64());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static List<long> Snapshot(List<long> delivered)
    {
        lock (delivered)
            return [.. delivered];
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Arrival);
        while (!condition())
            await Task.Delay(50, timeout.Token);
    }
}
