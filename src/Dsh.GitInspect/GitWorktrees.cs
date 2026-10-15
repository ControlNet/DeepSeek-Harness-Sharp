namespace Dsh.GitInspect;

/** One entry of `git worktree list --porcelain`; IsMain marks the main working tree (always listed first). */
public sealed record GitWorktree(
    string Path,
    string? Head,
    string? Branch,
    bool IsMain,
    bool IsBare,
    bool IsDetached,
    bool IsLocked,
    bool IsPrunable);

/**
 * Request for `git worktree add`.
 * NewBranch creates a branch at StartPoint (HEAD when null); without NewBranch, StartPoint may name an existing branch to check out.
 */
public sealed record GitWorktreeAddRequest(string Path, string? NewBranch = null, string? StartPoint = null);

/**
 * Parser for `git worktree list --porcelain`: attribute lines, records ended by an empty line.
 * With `-z` (git 2.36+) attributes are NUL terminated instead of newline terminated, so paths may contain newlines.
 */
internal static class GitWorktreeParser
{
    private const string BranchPrefix = "refs/heads/";

    public static IReadOnlyList<GitWorktree> Parse(string output, char separator)
    {
        var worktrees = new List<GitWorktree>();
        GitWorktree? current = null;
        foreach (var field in output.Split(separator))
        {
            if (field.Length == 0)
            {
                if (current is not null)
                    worktrees.Add(current with { IsMain = worktrees.Count == 0 });
                current = null;
                continue;
            }
            current = Apply(current, field);
        }
        if (current is not null)
            worktrees.Add(current with { IsMain = worktrees.Count == 0 });
        return worktrees;
    }

    private static GitWorktree? Apply(GitWorktree? current, string field)
    {
        var space = field.IndexOf(' ');
        var (key, value) = space < 0 ? (field, "") : (field[..space], field[(space + 1)..]);
        if (key == "worktree")
            return new GitWorktree(Path.GetFullPath(value), null, null, false, false, false, false, false);
        return current is null ? null : key switch
        {
            "HEAD" => current with { Head = value },
            "branch" => current with { Branch = value.StartsWith(BranchPrefix, StringComparison.Ordinal) ? value[BranchPrefix.Length..] : value },
            "bare" => current with { IsBare = true },
            "detached" => current with { IsDetached = true },
            "locked" => current with { IsLocked = true },
            "prunable" => current with { IsPrunable = true },
            _ => current,
        };
    }
}
