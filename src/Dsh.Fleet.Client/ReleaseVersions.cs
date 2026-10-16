namespace Dsh.Fleet;

/**
 * Release version comparison for deployment decisions.
 * A release is `[v]MAJOR.MINOR[.PATCH[.REVISION]]` optionally followed by `-prerelease` or `+build`; the suffix is ignored, so 1.2.0-rc1 counts as 1.2.0.
 */
public static class ReleaseVersions
{
    private static readonly char[] SuffixSeparators = ['-', '+'];

    /** The numeric part of a release, or null when the text is not a release version (empty, missing or garbled). */
    public static Version? Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];
        var cut = trimmed.IndexOfAny(SuffixSeparators);
        if (cut >= 0)
            trimmed = trimmed[..cut];
        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    /**
     * Whether an installed deployment can be kept for a client of release `current`.
     * Only the same or a newer release is kept; an older or unreadable installed version is replaced.
     */
    public static bool CanReuse(string? installed, string current)
        => Parse(installed) is { } have && Parse(current) is { } want && Normalize(have) >= Normalize(want);

    /** 1.2 and 1.2.0 are the same release: missing components count as zero. */
    private static Version Normalize(Version version)
        => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
}
