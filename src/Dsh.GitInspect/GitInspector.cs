using System.Globalization;

namespace Dsh.GitInspect;

/**
 * Read-mostly view of the git repository that contains a project directory, backed by the real git executable.
 * Every call starts a fresh git process, so results always reflect the current disk state (agents edit concurrently).
 */
public sealed class GitInspector(string directory)
{
    public const int DefaultLogCount = 50;

    /** First git release whose `worktree list` accepts `-z`. */
    private static readonly Version WorktreeListNulVersion = new(2, 36);

    /** Pathspec magic: resolve from the repository root and match the path literally (no glob characters). */
    /** "git version X.Y.Z": the third token is the version number. */
    private const int VersionTokenIndex = 2;

    private const string RootLiteralPathspec = ":(top,literal)";

    private static readonly Lazy<Task<Version>> InstalledVersion = new(ReadGitVersionAsync);

    public string Directory { get; } = Path.GetFullPath(directory);

    /** Top level of the working tree that contains Directory. */
    public async Task<string> GetRootAsync(CancellationToken cancellationToken = default)
        => Path.GetFullPath((await RunAsync(["rev-parse", "--show-toplevel"], cancellationToken)).Trim());

    public async Task<GitStatus> StatusAsync(CancellationToken cancellationToken = default)
        => GitStatusParser.Parse(await RunAsync(["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=normal"], cancellationToken));

    /** Most recent commits reachable from Revision (HEAD when null); empty when the branch has no commits yet. */
    public async Task<IReadOnlyList<GitCommitSummary>> LogAsync(
        int maxCount = DefaultLogCount,
        string? revision = null,
        CancellationToken cancellationToken = default)
    {
        string[] arguments = ["log", "-z", $"--max-count={maxCount.ToString(CultureInfo.InvariantCulture)}", GitLogParser.Format, revision ?? "HEAD", "--"];
        var result = await GitCommand.RunAsync(Directory, arguments, cancellationToken: cancellationToken);
        if (result.Ok)
            return GitLogParser.Parse(result.Output);
        if (revision is null && !await HasHeadCommitAsync(cancellationToken))
            return [];
        throw new GitCommandException("log", result.ExitCode, result.Error);
    }

    /**
     * Per-file unified diffs for Target, optionally restricted to Paths (repository-relative or absolute).
     * Patches are fetched file by file and cut at Limits, so a single huge file leaves the others readable.
     */
    public async Task<GitDiff> DiffAsync(
        GitDiffTarget target,
        GitDiffLimits? limits = null,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= GitDiffLimits.Default;
        var range = await RangeArgumentsAsync(target, cancellationToken);
        string[] listArguments = ["diff", .. range, "--raw", "--numstat", "-z", "-M", "--no-ext-diff", "--", .. paths ?? []];
        var entries = GitDiffEntryParser.Parse(await RunAsync(listArguments, cancellationToken));
        var files = new List<GitFileDiff>(entries.Count);
        var remaining = limits.MaxTotalChars;
        foreach (var entry in entries)
        {
            var file = await FilePatchAsync(range, entry, Math.Min(limits.MaxPatchChars, remaining), cancellationToken);
            remaining -= file.Patch.Length;
            files.Add(file);
        }
        return new GitDiff(target, files, files.Any(file => file.Truncated));
    }

    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(CancellationToken cancellationToken = default)
    {
        var nulSeparated = await InstalledVersion.Value >= WorktreeListNulVersion;
        string[] arguments = nulSeparated ? ["worktree", "list", "--porcelain", "-z"] : ["worktree", "list", "--porcelain"];
        return GitWorktreeParser.Parse(await RunAsync(arguments, cancellationToken), nulSeparated ? '\0' : '\n');
    }

    /** Adds a worktree (creating NewBranch when given) and returns its entry from the refreshed worktree list. */
    public async Task<GitWorktree> AddWorktreeAsync(GitWorktreeAddRequest request, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(request.Path, Directory);
        string[] branch = request.NewBranch is null ? [] : ["-b", request.NewBranch];
        string[] startPoint = request.StartPoint is null ? [] : [request.StartPoint];
        await RunAsync(["worktree", "add", .. branch, "--", path, .. startPoint], cancellationToken);
        var worktrees = await ListWorktreesAsync(cancellationToken);
        return worktrees.First(worktree => SamePath(worktree.Path, path));
    }

    /** Removes a linked worktree; Force also discards its uncommitted changes. */
    public async Task RemoveWorktreeAsync(string path, bool force = false, CancellationToken cancellationToken = default)
    {
        string[] forceArgument = force ? ["--force"] : [];
        await RunAsync(["worktree", "remove", .. forceArgument, "--", Path.GetFullPath(path, Directory)], cancellationToken);
    }

    private async Task<GitFileDiff> FilePatchAsync(
        IReadOnlyList<string> range,
        GitDiffEntry entry,
        int maxChars,
        CancellationToken cancellationToken)
    {
        // Diff paths are relative to the repository root while git resolves pathspecs from the working directory.
        string[] pathspec = entry.OriginalPath is null
            ? [$"{RootLiteralPathspec}{entry.Path}"]
            : [$"{RootLiteralPathspec}{entry.OriginalPath}", $"{RootLiteralPathspec}{entry.Path}"];
        string[] arguments = ["diff", .. range, "-M", "--no-color", "--no-ext-diff", "--", .. pathspec];
        var result = maxChars > 0
            ? await GitCommand.RunAsync(Directory, arguments, maxChars, cancellationToken)
            : new GitCommandResult(0, "", "", true);
        if (!result.Ok)
            throw new GitCommandException("diff", result.ExitCode, result.Error);
        return new GitFileDiff(entry.Path, entry.OriginalPath, entry.Kind, entry.Added, entry.Deleted, result.Output, result.OutputTruncated);
    }

    private async Task<IReadOnlyList<string>> RangeArgumentsAsync(GitDiffTarget target, CancellationToken cancellationToken)
    {
        switch (target.Source)
        {
            case GitDiffSource.WorkingTree:
                return [];
            case GitDiffSource.Staged:
                return ["--cached"];
            default:
                var commit = target.Commit ?? throw new ArgumentException("a commit diff needs a commit", nameof(target));
                var sha = (await RunAsync(["rev-parse", "--verify", "--end-of-options", $"{commit}^{{commit}}"], cancellationToken)).Trim();
                var parent = await GitCommand.RunAsync(Directory, ["rev-parse", "--verify", "-q", $"{sha}^1"], cancellationToken: cancellationToken);
                return [parent.Ok ? parent.Output.Trim() : await EmptyTreeAsync(cancellationToken), sha];
        }
    }

    /** Hash of the empty tree in this repository's object format (sha1 or sha256); stdin is closed, so git hashes nothing. */
    private async Task<string> EmptyTreeAsync(CancellationToken cancellationToken)
        => (await RunAsync(["hash-object", "-t", "tree", "--stdin"], cancellationToken)).Trim();

    private async Task<bool> HasHeadCommitAsync(CancellationToken cancellationToken)
    {
        var result = await GitCommand.RunAsync(Directory, ["rev-parse", "--verify", "-q", "HEAD"], cancellationToken: cancellationToken);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new GitCommandException("rev-parse", result.ExitCode, result.Error),
        };
    }

    private Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => GitCommand.RunCheckedAsync(Directory, arguments, cancellationToken);

    private static bool SamePath(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /** Parses "git version 2.34.1"; vendor suffixes such as ".windows.1" or " (Apple Git-146)" are ignored. */
    private static async Task<Version> ReadGitVersionAsync()
    {
        var output = await GitCommand.RunCheckedAsync(System.IO.Directory.GetCurrentDirectory(), ["version"]);
        var numbers = output.Split(' ', StringSplitOptions.RemoveEmptyEntries)[VersionTokenIndex].Split('.');
        return new Version(int.Parse(numbers[0], CultureInfo.InvariantCulture), int.Parse(numbers[1], CultureInfo.InvariantCulture));
    }
}
