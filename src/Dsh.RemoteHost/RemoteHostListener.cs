using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/**
 * loopback 监听: Unix domain socket(非 Windows) 或 TCP loopback + 端口文件(Windows); 逐连接服务。
 * Every connection must open with the daemon's LocalEndpointToken before any JSON-RPC frame; the stdio bridge presents it on behalf of the SSH client.
 */
public static class RemoteHostListener
{
    public static async Task ServeLoopbackAsync(
        RemoteHostServer server,
        string? root,
        CancellationToken cancellationToken)
    {
        // The token is issued into the owner-only run directory before the endpoint becomes reachable.
        using var token = LocalEndpointToken.Issue(RemoteHostEndpoint.EndpointFile(root));
        if (OperatingSystem.IsWindows())
            await ServeTcpAsync(server, root, token, cancellationToken).ConfigureAwait(false);
        else
            await ServeUnixAsync(server, root, token, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ServeTcpAsync(RemoteHostServer server, string? root, LocalEndpointToken token, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var portFile = RemoteHostEndpoint.PortFile(root);
        await File.WriteAllTextAsync(portFile, ((IPEndPoint)listener.LocalEndpoint).Port.ToString(), cancellationToken).ConfigureAwait(false);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (cancellationToken.IsCancellationRequested
                    && error is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // 取消时底层可能以 SocketException(而非 OperationCanceledException)结束挂起的 accept。
                    break;
                }

                // 每条连接独立服务: 一个半开/卡住的连接不能阻塞后续连接。
                _ = ServeConnectionAsync(server, new NetworkStream(client.Client, ownsSocket: true), client, token, cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
            TryDelete(portFile);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task ServeUnixAsync(RemoteHostServer server, string? root, LocalEndpointToken token, CancellationToken cancellationToken)
    {
        var socketPath = RemoteHostEndpoint.SocketPath(root);
        using var listener = OwnerOnlyFiles.ListenUnixSocket(socketPath, 8);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (cancellationToken.IsCancellationRequested
                    && error is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // 取消时底层可能以 SocketException(而非 OperationCanceledException)结束挂起的 accept。
                    break;
                }

                // 每条连接独立服务: 一个半开/卡住的连接不能阻塞后续连接。
                _ = ServeConnectionAsync(server, new NetworkStream(socket, ownsSocket: true), socket, token, cancellationToken);
            }
        }
        finally
        {
            TryDelete(socketPath);
        }
    }

    /**
     * 服务一条连接; 任何连接级异常只终结该连接, 不影响监听循环。
     * A connection without the matching token is closed before the host serves it.
     */
    private static async Task ServeConnectionAsync(RemoteHostServer server, Stream stream, IDisposable owner, LocalEndpointToken token, CancellationToken cancellationToken)
    {
        using (owner)
        await using (stream)
        {
            try
            {
                if (await PresentsTokenAsync(stream, token, cancellationToken).ConfigureAwait(false))
                    await server.ServeAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    /** Reads exactly the token's length, so no byte of the JSON-RPC stream behind it is consumed; an early EOF surfaces as EndOfStreamException. */
    private static async Task<bool> PresentsTokenAsync(Stream stream, LocalEndpointToken token, CancellationToken cancellationToken)
    {
        var preamble = new byte[LocalEndpointToken.ValueLength];
        await stream.ReadExactlyAsync(preamble, cancellationToken).ConfigureAwait(false);
        return token.Matches(Encoding.UTF8.GetString(preamble));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /** 常驻 daemon 是否可达: 短暂重试覆盖“正在重启/刚绑定”的窗口, 避免误判不可达而重复 spawn daemon。 */
    public static async Task<bool> IsReachableAsync(string? root, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await ConnectAsync(root, cancellationToken, attempts: 10).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is SocketException or IOException or TimeoutException)
        {
            return false;
        }
    }

    /** 连接到常驻 daemon 并出示其 token; 带重试, 覆盖“刚 spawn 起来还没 bind”的窗口。 */
    public static async Task<Stream> ConnectAsync(string? root, CancellationToken cancellationToken, int attempts = 50)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var stream = await ConnectEndpointAsync(root, cancellationToken).ConfigureAwait(false);
                return await PresentTokenAsync(stream, root, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SocketException or IOException or FormatException)
            {
                last = error;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("host daemon not reachable", last);
    }

    private static async Task<Stream> ConnectEndpointAsync(string? root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return await TransportConnection.ConnectUnixAsync(RemoteHostEndpoint.SocketPath(root), cancellationToken).ConfigureAwait(false);
        var portFile = RemoteHostEndpoint.PortFile(root);
        if (!File.Exists(portFile))
            throw new IOException("host daemon not started");
        var port = int.Parse((await File.ReadAllTextAsync(portFile, cancellationToken).ConfigureAwait(false)).Trim());
        // A refused loopback connect takes about 2 s on Windows (SYN retransmits); the port file a killed host left behind made every reconnect attempt that slow.
        if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener => listener.Port == port))
            throw new IOException("host daemon not listening");
        return await TransportConnection.ConnectTcpAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
    }

    /**
     * Sends the token the daemon issued before it started listening; it is read only after the connect succeeded, because a daemon that is not running has no token yet.
     * A reachable daemon without a token is not retried as unreachable: that would spawn a second daemon that deletes the live socket.
     */
    private static async Task<Stream> PresentTokenAsync(Stream stream, string? root, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = RemoteHostEndpoint.EndpointFile(root);
            var token = LocalEndpointToken.Read(endpoint)
                ?? throw new InvalidOperationException(
                    $"the dsharp host daemon at {endpoint} issued no access token ({LocalEndpointToken.PathFor(endpoint)}); it was started by an older build, stop that daemon process and retry");
            await stream.WriteAsync(Encoding.UTF8.GetBytes(token), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /** 把一条 stdio 双向流桥接到常驻 daemon: ssh 会话断开只断这条桥, daemon 与其上的会话继续跑(脱钩/重连)。 */
    public static async Task BridgeAsync(Stream duplex, string? root, CancellationToken cancellationToken)
    {
        using var daemon = await ConnectAsync(root, cancellationToken).ConfigureAwait(false);
        var toDaemon = PumpAsync(duplex, daemon, cancellationToken);
        var toClient = PumpAsync(daemon, duplex, cancellationToken);
        await Task.WhenAny(toDaemon, toClient).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await to.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 对端关闭/取消/流被释放: 泵结束即可。
        }
    }
}
