namespace Dsh.RemoteHost;

/** Optional agent lifecycle and interaction capabilities. */
public interface IRemoteAgent
{
    /** Agent kinds this host can start (needs agent.sessions). */
    Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken = default);

    /** Starts any kind of session (harness, terminal-mode or ACP-mode agent) and returns its registry record. */
    Task<RemoteHostSession> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken = default);

    /** One user turn for an ACP or harness session; events arrive through IRemoteHost.SubscribeAsync. */
    Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken = default);

    Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken = default);

    Task StopAgentAsync(string sessionId, CancellationToken cancellationToken = default);

    /** Restarts an exited agent session; terminal-mode agents come back under a new session id. */
    Task<RemoteHostSession> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken = default);
}
