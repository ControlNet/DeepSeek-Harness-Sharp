namespace Dsh.Transport;

/**
 * The per-user root that local daemons (PTY daemon, remote host) place their run directory under.
 * It is $DSH_HOME or ~/.dsh, redirected to the recorded root when that directory holds a .storage-root marker (settings.storage.root).
 * Daemons and their clients must resolve it the same way, otherwise one binds its socket under the default root while the other looks under storage.root.
 */
public static class LocalEndpointRoot
{
    /** Same name as Dsh.Boot.HarnessHome.StorageRootMarker; Transport cannot reference Boot, so the literal is repeated here. */
    public const string StorageRootMarker = ".storage-root";

    private const string HomeVariable = "DSH_HOME";
    private const string DefaultDirectoryName = ".dsh";

    public static string Resolve()
    {
        var home = Environment.GetEnvironmentVariable(HomeVariable);
        var root = string.IsNullOrWhiteSpace(home)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
                DefaultDirectoryName)
            : home;
        return FollowMarker(root);
    }

    /** Returns the root recorded in root/.storage-root when present, otherwise root itself; both as full paths. */
    public static string FollowMarker(string root)
    {
        try
        {
            var marker = Path.Combine(root, StorageRootMarker);
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() is { Length: > 0 } recorded)
                return Path.GetFullPath(recorded);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return Path.GetFullPath(root);
    }
}
