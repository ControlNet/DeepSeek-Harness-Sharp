using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.WebBridge;

/** Where a running web bridge announces itself inside the harness home's owner-only run directory: its port file and, next to it, the web access token. */
public static class WebBridgeEndpoint
{
    /** Port used when `dsharp host web` gets no --port. */
    public const int DefaultPort = 8780;

    public static string PortFile(string? root) => Path.Combine(RemoteHostEndpoint.RunDirectory(root), "web.port");

    /** The web access token is a LocalEndpointToken of the port file, distinct from the host.sock token. */
    public static string TokenFile(string? root) => LocalEndpointToken.PathFor(PortFile(root));

    /** The address the bridge listens on; also what a browser opens through an ssh -L forward of the same port. */
    public static string LocalUrl(int port) => $"http://127.0.0.1:{port}/";
}
