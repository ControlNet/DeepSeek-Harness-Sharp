using Dsh.GitInspect;

namespace Dsh.RemoteHost;

/** Optional repository inspection and worktree capabilities. */
public interface IRemoteGit
{
    /** host.git.*: ProjectPath is any directory inside the repository (needs git.inspect). */
    Task<GitStatus> GitStatusAsync(string projectPath, CancellationToken cancellationToken = default);

    Task<GitDiff> GitDiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitCommitSummary>> GitLogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string projectPath, CancellationToken cancellationToken = default);

    Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken = default);

    Task RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken = default);
}
