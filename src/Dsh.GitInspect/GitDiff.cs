namespace Dsh.GitInspect;

public enum GitDiffSource
{
    /** Index versus working tree, i.e. unstaged changes (`git diff`). */
    WorkingTree,

    /** HEAD versus index, i.e. staged changes (`git diff --cached`). */
    Staged,

    /** One commit versus its first parent (versus the empty tree for a root commit). */
    Commit,
}

public sealed record GitDiffTarget(GitDiffSource Source, string? Commit = null)
{
    public static GitDiffTarget WorkingTree { get; } = new(GitDiffSource.WorkingTree);

    public static GitDiffTarget Staged { get; } = new(GitDiffSource.Staged);

    public static GitDiffTarget OfCommit(string commit) => new(GitDiffSource.Commit, commit);
}

/** Size caps in UTF-16 chars: per file patch and for the whole response, so one generated file cannot flood a remote frame. */
public sealed record GitDiffLimits(int MaxPatchChars, int MaxTotalChars)
{
    public const int DefaultMaxPatchChars = 256 * 1024;
    public const int DefaultMaxTotalChars = 4 * 1024 * 1024;

    public static GitDiffLimits Default { get; } = new(DefaultMaxPatchChars, DefaultMaxTotalChars);
}

/**
 * Unified diff of one file.
 * Added/Deleted are line counts and are null for binary files; Truncated means Patch was cut at a size cap (possibly to empty).
 */
public sealed record GitFileDiff(
    string Path,
    string? OriginalPath,
    GitChangeKind Kind,
    int? Added,
    int? Deleted,
    string Patch,
    bool Truncated)
{
    public bool IsBinary => Added is null;
}

/** Truncated is true when any file patch was cut. */
public sealed record GitDiff(GitDiffTarget Target, IReadOnlyList<GitFileDiff> Files, bool Truncated);

/** A changed path enumerated before patches are fetched. */
internal sealed record GitDiffEntry(string Path, string? OriginalPath, GitChangeKind Kind, int? Added, int? Deleted);

/**
 * Parser for `git diff --raw --numstat -z`: raw records (starting with ':') come first, numstat records follow.
 * Raw gives the status letter, numstat the line counts; both are keyed by the post-image path.
 */
internal static class GitDiffEntryParser
{
    private const char RawPrefix = ':';
    private const string BinaryCount = "-";
    private const int NumstatFieldCount = 3;

    public static IReadOnlyList<GitDiffEntry> Parse(string output)
    {
        var fields = output.Split('\0');
        var index = 0;
        var raw = new List<(string Path, string? OriginalPath, GitChangeKind Kind)>();
        while (index < fields.Length && fields[index].StartsWith(RawPrefix))
        {
            var kind = GitStatusParser.KindOf(fields[index].Split(' ')[^1][0]);
            var renamed = kind is GitChangeKind.Renamed or GitChangeKind.Copied;
            raw.Add(renamed ? (fields[index + 2], fields[index + 1], kind) : (fields[index + 1], null, kind));
            index += renamed ? 3 : 2;
        }
        var counts = ParseNumstat(fields, index);
        return raw
            .Select(entry => counts.TryGetValue(entry.Path, out var count)
                ? new GitDiffEntry(entry.Path, entry.OriginalPath, entry.Kind, count.Added, count.Deleted)
                : new GitDiffEntry(entry.Path, entry.OriginalPath, entry.Kind, 0, 0))
            .ToList();
    }

    /** Numstat -z: "added\tdeleted\tpath" or, for renames, "added\tdeleted\t" followed by old and new path fields. */
    private static Dictionary<string, (int? Added, int? Deleted)> ParseNumstat(string[] fields, int index)
    {
        var counts = new Dictionary<string, (int? Added, int? Deleted)>(StringComparer.Ordinal);
        while (index < fields.Length && fields[index].Length > 0)
        {
            var parts = fields[index].Split('\t', NumstatFieldCount);
            var path = parts[2].Length > 0 ? parts[2] : fields[index + 2];
            counts[path] = (CountOf(parts[0]), CountOf(parts[1]));
            index += parts[2].Length > 0 ? 1 : 3;
        }
        return counts;
    }

    private static int? CountOf(string text) => text == BinaryCount ? null : int.Parse(text);
}
