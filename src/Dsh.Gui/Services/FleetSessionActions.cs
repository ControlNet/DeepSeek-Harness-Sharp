using Dsh.Fleet;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/** The model a harness session on any machine is started or resumed with: the GUI's current provider and model, and the provider's endpoint so a host without that provider can register it. */
public readonly record struct HarnessModelChoice(RemoteModelSelection? Selection, RemoteProviderSpec? Provider);

/**
 * Lifecycle operations on fleet sessions, shared by the dashboard, the session view and the command palette.
 * Every call goes through the machine's current connection and waits for it to be connected; failures surface as exceptions for the caller to report.
 */
public sealed class FleetSessionActions(FleetWorkspace fleet, Func<HarnessModelChoice> harnessModel)
{
    public HarnessModelChoice HarnessModel => harnessModel();

    public async Task<HostLease> LeaseAsync(string machine, CancellationToken cancellationToken)
        => await Connection(machine).WaitConnectedAsync(cancellationToken);

    private MachineConnection Connection(string machine)
        => fleet.Find(machine) ?? throw new InvalidOperationException($"no machine named \"{machine}\"");

    /** Harness sessions release their agent, terminals and external agents end their process, ACP agents end their adapter. */
    public async Task StopAsync(string machine, string sessionId, CancellationToken cancellationToken)
    {
        var lease = await LeaseAsync(machine, cancellationToken);
        await lease.Client.StopAgentAsync(sessionId, cancellationToken);
    }

    /** Resumes an exited session; a terminal-mode agent comes back under a new session id, which the returned record carries. */
    public async Task<RemoteHostSession> ResumeAsync(string machine, RemoteHostSession session, CancellationToken cancellationToken)
    {
        var lease = await LeaseAsync(machine, cancellationToken);
        RemoteHostSession resumed;
        if (session.Kind != HostSessionKinds.Harness)
            resumed = await lease.Client.ResumeAgentAsync(session.SessionId, cancellationToken);
        else
        {
            var (selection, provider) = HarnessModel;
            await lease.Client.ResumeSessionAsync(session.SessionId, selection, provider, cancellationToken);
            resumed = (await lease.Client.ListHostSessionsAsync(cancellationToken)).FirstOrDefault(item => item.SessionId == session.SessionId) ?? session;
        }
        Connection(machine).Remember(resumed);
        return resumed;
    }

    /** Starts a session of any kind; harness sessions use the GUI's current model unless the request names one. */
    public async Task<RemoteHostSession> StartAsync(string machine, RemoteAgentStartRequest request, CancellationToken cancellationToken)
    {
        var lease = await LeaseAsync(machine, cancellationToken);
        if (request.AgentKind == HostAgentKinds.Dsharp && request.Selection is null)
        {
            var (selection, provider) = HarnessModel;
            request = request with { Selection = selection, Provider = provider };
        }
        var started = await lease.Client.StartAgentAsync(request, cancellationToken);
        Connection(machine).Remember(started);
        return started;
    }
}
