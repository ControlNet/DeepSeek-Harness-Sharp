namespace Dsh.RemoteHost;

/** A host composes its required session/decision/registry services and explicitly supplies optional domains. */
public interface IRemoteHostBackend
{
    HostPendingDecisions Pending { get; }
    HostSessionRegistry Sessions { get; }
    IRemoteSessionBackend Session { get; }
    Task<HostInfo> InfoAsync(CancellationToken cancellationToken);

    IRemoteAgentBackend? Agent { get; }
    IRemotePtyBackend? Pty { get; }
    HostFileSystem? Files { get; }
    HostGitService? Git { get; }
    HostNotificationCenter? Notifications { get; }
    IRemoteSessionEditingBackend? SessionEditing { get; }
    IRemotePluginsBackend? Plugins { get; }
    IRemoteModelsBackend? Models { get; }
}

/** Required session execution and event stream. Subscribe installs its live listener before returning. */
public interface IRemoteSessionBackend
{
    Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken);
    Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken);
    Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken);
    Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken);
    Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken);
    Task InterruptAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken);
    Task<IAsyncEnumerable<RemoteEventInfo>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken);
}

public interface IRemoteSessionEditingBackend
{
    Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken);
    Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken);
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken);
}

public interface IRemoteAgentBackend
{
    Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken);
    Task<RemoteHostSession> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken);
    Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken);
    Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken);
    Task StopAgentAsync(string sessionId, CancellationToken cancellationToken);
    Task<RemoteHostSession> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken);
}

public interface IRemotePtyBackend
{
    Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken);
    Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken);
    Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken);
    Task StopPtyAsync(string ptyId, CancellationToken cancellationToken);
    Task<IAsyncEnumerable<RemotePtyOutput>> AttachPtyAsync(string ptyId, CancellationToken cancellationToken);
}

public interface IRemotePluginsBackend
{
    Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken);
    Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken);
}

public interface IRemoteModelsBackend
{
    Task<IReadOnlyList<string>> ListReasoningEffortsAsync(string provider, string model, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListProviderModelsAsync(string provider, CancellationToken cancellationToken);
}
