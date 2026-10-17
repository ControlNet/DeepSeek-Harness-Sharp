namespace Dsh.RemoteHost;

/** Optional plugin inspection and permission editing capabilities. */
public interface IRemotePlugins
{
    Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken = default);

    Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken = default);
}
