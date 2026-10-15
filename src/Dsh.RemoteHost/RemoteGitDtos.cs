using Dsh.GitInspect;

namespace Dsh.RemoteHost;

/** host.git.status / host.git.worktree.list: ProjectPath is any directory inside the repository. */
public sealed record RemoteGitRequest(string ProjectPath);

public sealed record RemoteGitDiffRequest(
    string ProjectPath,
    GitDiffTarget Target,
    IReadOnlyList<string>? Paths = null,
    GitDiffLimits? Limits = null);

public sealed record RemoteGitLogRequest(string ProjectPath, int? MaxCount = null, string? Revision = null);

public sealed record RemoteGitWorktreeAdd(string ProjectPath, string Path, string? NewBranch = null, string? StartPoint = null);

public sealed record RemoteGitWorktreeRemove(string ProjectPath, string Path, bool Force = false);
