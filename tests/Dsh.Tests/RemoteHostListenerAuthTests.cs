using System.Net;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

/** Owner-only endpoint and token preamble of the resident remote host daemon, exercised over its real loopback endpoint. */
public sealed class RemoteHostListenerAuthTests
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerAll = OwnerReadWrite | UnixFileMode.UserExecute;

    [Fact(Timeout = 30000)]
    public async Task Serve_RestrictsSocketTokenAndRunDirectoryToOwner()
    {
        await WithDaemonAsync("rp", TestContext.Current.CancellationToken, root =>
        {
            if (OperatingSystem.IsWindows())
                return Task.CompletedTask;
            Assert.Equal(OwnerAll, File.GetUnixFileMode(RemoteHostEndpoint.RunDirectory(root)));
            Assert.Equal(OwnerReadWrite, File.GetUnixFileMode(RemoteHostEndpoint.SocketPath(root)));
            Assert.Equal(OwnerReadWrite, File.GetUnixFileMode(LocalEndpointToken.PathFor(RemoteHostEndpoint.SocketPath(root))));
            return Task.CompletedTask;
        });
    }

    [Fact(Timeout = 30000)]
    public async Task ClientWithoutToken_IsDisconnected_AndDaemonKeepsServing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithDaemonAsync("ru", cancellationToken, async root =>
        {
            await using (var raw = await ConnectRawAsync(root, cancellationToken))
                await Assert.ThrowsAnyAsync<IOException>(() => RemoteHostClient.ConnectAsync(raw, null, cancellationToken));

            await using var stream = await RemoteHostListener.ConnectAsync(root, cancellationToken);
            await using var client = await RemoteHostClient.ConnectAsync(stream, null, cancellationToken);
            Assert.Equal(HostProtocol.Version, (await client.InfoAsync(cancellationToken)).ProtocolVersion);
        });
    }

    [Fact(Timeout = 30000)]
    public async Task Connect_WhenDaemonIssuedNoToken_FailsWithExplicitError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithDaemonAsync("rt", cancellationToken, async root =>
        {
            var endpoint = OperatingSystem.IsWindows() ? RemoteHostEndpoint.PortFile(root) : RemoteHostEndpoint.SocketPath(root);
            File.Delete(LocalEndpointToken.PathFor(endpoint));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteHostListener.ConnectAsync(root, cancellationToken));
            Assert.Contains(LocalEndpointToken.PathFor(endpoint), error.Message);
        });
    }

    private static async Task WithDaemonAsync(string prefix, CancellationToken cancellationToken, Func<string, Task> body)
    {
        // Socket paths are capped at 108 characters, so the daemon root is a shallow directory inside the repository.
        var root = TempTree.CreateSocketDirectory(prefix);
        using var daemonCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var server = new RemoteHostServer(new RemoteHostServerOptions());
        var daemon = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(server, root, daemonCts.Token), cancellationToken);
        try
        {
            // A successful connect proves the endpoint is bound before the test inspects it.
            await (await RemoteHostListener.ConnectAsync(root, cancellationToken)).DisposeAsync();
            await body(root);
        }
        finally
        {
            await daemonCts.CancelAsync();
            try
            {
                await daemon;
            }
            catch (OperationCanceledException)
            {
            }

            TempTree.Delete(root);
        }
    }

    /** Connects to the daemon endpoint without presenting the token, like any other local process could. */
    private static async Task<Stream> ConnectRawAsync(string root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return await TransportConnection.ConnectUnixAsync(RemoteHostEndpoint.SocketPath(root), cancellationToken);
        var port = int.Parse(await File.ReadAllTextAsync(RemoteHostEndpoint.PortFile(root), cancellationToken));
        return await TransportConnection.ConnectTcpAsync(IPAddress.Loopback.ToString(), port, cancellationToken);
    }
}
