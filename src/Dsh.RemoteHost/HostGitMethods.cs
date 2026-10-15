using System.Text.Json;
using Dsh.GitInspect;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** host.git.*: Dsh.GitInspect on a project directory; the project path and every worktree path must pass the host's path policy. */
internal static class HostGitMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(HostPathPolicy policy, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case HostProtocol.MethodGitStatus:
            {
                var inspector = Inspector(policy, Require(message, HostProtocolJsonContext.Default.RemoteGitRequest, "git status requires projectPath").ProjectPath);
                return Serialize(await inspector.StatusAsync(cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.GitStatus);
            }
            case HostProtocol.MethodGitDiff:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteGitDiffRequest, "git diff requires projectPath and target");
                var diff = await Inspector(policy, request.ProjectPath).DiffAsync(request.Target, request.Limits, request.Paths, cancellationToken).ConfigureAwait(false);
                return Serialize(diff, HostProtocolJsonContext.Default.GitDiff);
            }
            case HostProtocol.MethodGitLog:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteGitLogRequest, "git log requires projectPath");
                var commits = await Inspector(policy, request.ProjectPath).LogAsync(request.MaxCount ?? GitInspector.DefaultLogCount, request.Revision, cancellationToken).ConfigureAwait(false);
                return Serialize(commits, HostProtocolJsonContext.Default.IReadOnlyListGitCommitSummary);
            }
            default:
                return await HandleWorktreeAsync(policy, name, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<JsonElement?> HandleWorktreeAsync(HostPathPolicy policy, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case HostProtocol.MethodGitWorktreeList:
            {
                var inspector = Inspector(policy, Require(message, HostProtocolJsonContext.Default.RemoteGitRequest, "worktree list requires projectPath").ProjectPath);
                return Serialize(await inspector.ListWorktreesAsync(cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.IReadOnlyListGitWorktree);
            }
            case HostProtocol.MethodGitWorktreeAdd:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteGitWorktreeAdd, "worktree add requires projectPath and path");
                var added = new GitWorktreeAddRequest(policy.Resolve(request.Path), request.NewBranch, request.StartPoint);
                return Serialize(await Inspector(policy, request.ProjectPath).AddWorktreeAsync(added, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.GitWorktree);
            }
            case HostProtocol.MethodGitWorktreeRemove:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteGitWorktreeRemove, "worktree remove requires projectPath and path");
                await Inspector(policy, request.ProjectPath).RemoveWorktreeAsync(policy.Resolve(request.Path), request.Force, cancellationToken).ConfigureAwait(false);
                return True;
            }
            default:
                throw MethodNotFound(name);
        }
    }

    private static GitInspector Inspector(HostPathPolicy policy, string projectPath) => new(policy.Resolve(projectPath));
}
