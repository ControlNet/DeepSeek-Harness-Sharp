namespace Dsh.Core;

/** A logged change of the session's effective working directory; the header cwd is never rewritten. */
public sealed record WorkingDirectoryChangedPayload(string Cwd) : SessionEventPayload
{
    public override string Type => SessionEventTypes.WorkingDirectoryChange;
}

/**
 * Session working directory and write permission root.
 * The effective directory (Session.WorkingDirectory) is the base for relative paths and the default directory of newly started processes.
 * The header cwd is the write permission root and does not follow directory changes.
 */
public static class SessionWorkingDirectory
{
    public const string OutsideRootReason = "目标路径在权限根之外";

    /** Resolves requested against the current directory, requires an existing directory and logs the change. */
    public static string Change(Session session, string requested)
    {
        var current = session.WorkingDirectory ?? Environment.CurrentDirectory;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested, current));
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException($"not an existing directory: {target}");
        if (!string.Equals(target, session.WorkingDirectory, StringComparison.Ordinal))
            session.Append(new WorkingDirectoryChangedPayload(target));
        return target;
    }

    public static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "."
            || !(Path.IsPathRooted(relative)
                || relative == ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    /** Approval reason naming the first target outside the write permission root; null when every target is inside or the session has no root. */
    public static string? OutsideRootApprovalReason(Session session, IEnumerable<string> targets)
    {
        if (session.Header.Cwd is not { } root)
            return null;
        var outside = targets.FirstOrDefault(target => !IsWithin(root, target));
        return outside is null ? null : $"{OutsideRootReason}：{outside}（权限根：{root}）";
    }
}
