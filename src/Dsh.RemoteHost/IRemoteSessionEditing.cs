namespace Dsh.RemoteHost;

/** Optional session commands, metadata editing, and subagent inspection capabilities. */
public interface IRemoteSessionEditing
{
    Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken = default);

    Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken = default);
}
