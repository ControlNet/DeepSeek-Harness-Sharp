using Dsh.GitInspect;

namespace Dsh.RemoteHost;

/** Repository operations with the host's project and worktree path boundary. */
public sealed class HostGitService(HostPathPolicy policy)
{
    public Task<GitStatus> StatusAsync(RemoteGitRequest request, CancellationToken cancellationToken)
        => Inspector(request.ProjectPath).StatusAsync(cancellationToken);

    public Task<GitDiff> DiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken)
        => Inspector(request.ProjectPath).DiffAsync(request.Target, request.Limits, request.Paths, cancellationToken);

    public Task<IReadOnlyList<GitCommitSummary>> LogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken)
        => Inspector(request.ProjectPath).LogAsync(request.MaxCount ?? GitInspector.DefaultLogCount, request.Revision, cancellationToken);

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(RemoteGitRequest request, CancellationToken cancellationToken)
        => Inspector(request.ProjectPath).ListWorktreesAsync(cancellationToken);

    public Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken)
        => Inspector(request.ProjectPath).AddWorktreeAsync(new GitWorktreeAddRequest(policy.Resolve(request.Path), request.NewBranch, request.StartPoint), cancellationToken);

    public Task RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken)
        => Inspector(request.ProjectPath).RemoveWorktreeAsync(policy.Resolve(request.Path), request.Force, cancellationToken);

    private GitInspector Inspector(string projectPath) => new(policy.Resolve(projectPath));
}
