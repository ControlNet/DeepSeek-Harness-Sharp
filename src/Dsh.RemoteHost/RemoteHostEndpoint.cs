namespace Dsh.RemoteHost;

/**
 * 远端宿主的端点约定(与 Pty daemon 同款: DSH_HOME/run 下的 Unix socket / Windows 端口文件)。
 * stdio-over-SSH 不需要端点; loopback socket 供端口转发或本机连接使用(§14 §4)。
 */
public static class RemoteHostEndpoint
{
    public static string DefaultRoot()
    {
        var home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return home;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
    }

    public static string RunDirectory(string? root = null) => Path.Combine(root ?? DefaultRoot(), "run");

    public static string SocketPath(string? root = null) => Path.Combine(RunDirectory(root), "host.sock");

    public static string PortFile(string? root = null) => Path.Combine(RunDirectory(root), "host.port");

    /** The file that announces the endpoint on this platform (socket or port file); the daemon's LocalEndpointToken sits next to it. */
    public static string EndpointFile(string? root = null) => OperatingSystem.IsWindows() ? PortFile(root) : SocketPath(root);
}
