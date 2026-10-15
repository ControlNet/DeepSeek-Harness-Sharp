using System.Net;
using System.Net.WebSockets;
using Dsh.RemoteHost;
using Dsh.Transport;
using Dsh.WebBridge;

namespace Dsh.Tests;

/** The browser bridge end to end: a real in-process host daemon on its loopback endpoint, the bridge on a free loopback port, and real HTTP and WebSocket clients. */
public sealed class WebBridgeTests
{
    private const string ForeignOrigin = "http://evil.example";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30000)]
    public async Task WebSocket_RelaysHostInfoAndPushedSessionEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = await fixture.Access.ConnectWebSocketAsync(await fixture.Access.LoginAsync(cancellationToken), fixture.Access.Origin, cancellationToken);

        var info = await WebSocketRpc.RequestAsync(socket, 1, HostProtocol.MethodInfo, null);
        Assert.Equal(HostProtocol.Version, info.GetProperty("ProtocolVersion").GetInt32());
        Assert.Equal("fake", info.GetProperty("HostVersion").GetString());

        var created = await WebSocketRpc.RequestAsync(socket, 2, HostProtocol.MethodSessionCreate, """{"Cwd":"/work"}""");
        var sessionId = created.GetProperty("Id").GetString();
        await WebSocketRpc.RequestAsync(socket, 3, HostProtocol.MethodSessionSubscribe, $$"""{"SessionId":"{{sessionId}}","FromSeq":0}""");
        await WebSocketRpc.SendAsync(socket, 4, HostProtocol.MethodSessionMessage, $$"""{"SessionId":"{{sessionId}}","Text":"hi"}""");
        var pushed = await WebSocketRpc.ReceiveUntilAsync(socket, message => message.TryGetProperty("method", out var method) && method.GetString() == HostProtocol.NotificationEvent);

        Assert.False(pushed.TryGetProperty("id", out _));
        Assert.Equal(sessionId, pushed.GetProperty("params").GetProperty("SessionId").GetString());
        Assert.Equal("user:hi", pushed.GetProperty("params").GetProperty("EventJson").GetString());
    }

    [Fact(Timeout = 30000)]
    public async Task WebSocket_WithoutSessionCookie_IsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = BridgeAccess.CreateWebSocket(new CookieContainer(), fixture.Access.Origin);

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(fixture.Access.WebSocketUri, cancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, socket.HttpStatusCode);
        Assert.Equal(0, fixture.RelayedConnections);
    }

    [Fact(Timeout = 30000)]
    public async Task WebSocket_FromForeignOrigin_IsRefusedEvenWithSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = BridgeAccess.CreateWebSocket(await fixture.Access.LoginAsync(cancellationToken), ForeignOrigin);

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(fixture.Access.WebSocketUri, cancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, socket.HttpStatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task Login_ExchangesTokenForStrictHttpOnlySessionCookie()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        var cookies = new CookieContainer();
        using var client = BridgeAccess.CreateHttpClient(cookies);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(fixture.Access.Url(WebBridgeServer.SessionPath), cancellationToken)).StatusCode);

        using var wrongToken = await fixture.Access.PostLoginAsync(client, "0123", fixture.Access.Origin, cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        using var crossSite = await fixture.Access.PostLoginAsync(client, fixture.Token, ForeignOrigin, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Empty(cookies.GetAllCookies());

        using var accepted = await fixture.Access.PostLoginAsync(client, fixture.Token, fixture.Access.Origin, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        var setCookie = Assert.Single(accepted.Headers.GetValues("Set-Cookie"));
        Assert.Contains("HttpOnly", setCookie, StringComparison.Ordinal);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Token, setCookie, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(fixture.Access.Url(WebBridgeServer.SessionPath), cancellationToken)).StatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task HostHeader_MustNameLoopback_ButMayCarryAForwardedPort()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var client = BridgeAccess.CreateHttpClient(new CookieContainer());

        Assert.Equal(HttpStatusCode.Forbidden, await GetWithHostAsync(client, fixture, $"evil.example:{fixture.Bridge.Port}"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetWithHostAsync(client, fixture, $"127.0.0.1.evil.example:{fixture.Bridge.Port}"));
        Assert.Equal(HttpStatusCode.OK, await GetWithHostAsync(client, fixture, "localhost:9999"));
    }

    [Fact(Timeout = 30000)]
    public async Task FrontEnd_IsServedFromEmbeddedAssetsWithSecurityHeaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var client = BridgeAccess.CreateHttpClient(new CookieContainer());

        using var page = await client.GetAsync(fixture.Access.Url("/"), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("frame-ancestors 'none'", Assert.Single(page.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.Contains("app.js", await page.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);

        using var script = await client.GetAsync(fixture.Access.Url("/rpc.js"), cancellationToken);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(fixture.Access.Url("/missing.js"), cancellationToken)).StatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task ClosingWebSocket_ReleasesItsHostConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = await fixture.Access.ConnectWebSocketAsync(await fixture.Access.LoginAsync(cancellationToken), fixture.Access.Origin, cancellationToken);
        await WebSocketRpc.RequestAsync(socket, 1, HostProtocol.MethodInfo, null);
        Assert.Equal(1, fixture.RelayedConnections);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);

        await WebSocketRpc.WaitForAsync(() => fixture.RelayedConnections == 0);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact(Timeout = 30000)]
    public async Task HostShutdown_ClosesWebSocketAsEndpointUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = await fixture.Access.ConnectWebSocketAsync(await fixture.Access.LoginAsync(cancellationToken), fixture.Access.Origin, cancellationToken);
        await WebSocketRpc.RequestAsync(socket, 1, HostProtocol.MethodInfo, null);

        await fixture.StopDaemonAsync();
        var received = await socket.ReceiveAsync(new byte[1024], cancellationToken);

        Assert.Equal(WebSocketMessageType.Close, received.MessageType);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);
    }

    [Fact(Timeout = 30000)]
    public async Task WebSocket_WithoutHostDaemon_ClosesWithReason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken, withDaemon: false);
        using var socket = await fixture.Access.ConnectWebSocketAsync(await fixture.Access.LoginAsync(cancellationToken), fixture.Access.Origin, cancellationToken);

        var received = await socket.ReceiveAsync(new byte[1024], cancellationToken);

        Assert.Equal(WebSocketMessageType.Close, received.MessageType);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, socket.CloseStatus);
        Assert.Contains("not reachable", socket.CloseStatusDescription, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30000)]
    public async Task PrintToken_PrintsRunningBridgeTokenAndLoginUrl()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        var output = new StringWriter();

        var exit = await WebBridgeCommand.RunAsync(fixture.Root, [WebBridgeCommand.PrintTokenOption], output, new StringWriter(), NoHost, cancellationToken);

        string[] expected = [fixture.Token, $"{WebBridgeEndpoint.LocalUrl(fixture.Bridge.Port)}#token={fixture.Token}"];
        Assert.Equal(0, exit);
        Assert.Equal(expected, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact(Timeout = 30000)]
    public async Task Command_StartsBridgeAndPrintsAccessHints_ThenRemovesTokenOnStop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("wc");
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var output = new StringWriter();
            var hostEnsured = false;
            var command = WebBridgeCommand.RunAsync(root, [WebBridgeCommand.PortOption, "0"], output, new StringWriter(), () =>
            {
                hostEnsured = true;
                return Task.CompletedTask;
            }, stop.Token);
            await WebSocketRpc.WaitForAsync(() => File.Exists(WebBridgeEndpoint.TokenFile(root)) && output.ToString().Contains("ssh -N -L", StringComparison.Ordinal));

            Assert.True(hostEnsured);
            Assert.Contains(WebBridgeEndpoint.TokenFile(root), output.ToString(), StringComparison.Ordinal);
            await stop.CancelAsync();
            Assert.Equal(0, await command);
            Assert.False(File.Exists(WebBridgeEndpoint.TokenFile(root)));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Command_RejectsUnknownArgumentsAndMissingBridge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("wa");
        try
        {
            var error = new StringWriter();
            Assert.Equal(2, await WebBridgeCommand.RunAsync(root, ["--port", "notaport"], new StringWriter(), error, NoHost, cancellationToken));
            Assert.Equal(1, await WebBridgeCommand.RunAsync(root, [WebBridgeCommand.PrintTokenOption], new StringWriter(), error, NoHost, cancellationToken));
            Assert.Contains("no web bridge is running", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** Opens the real page in headless Chrome with the one-time #token link and checks that it logs in, connects and shows the version the machine's host reports in host.hello. */
    [Fact(Timeout = 120000)]
    public async Task HeadlessChrome_LogsInThroughFragmentAndShowsHostInfo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        var artifacts = HeadlessChrome.ArtifactDirectory("web-bridge");
        await using var chrome = await HeadlessChrome.StartAsync(artifacts, cancellationToken);

        await chrome.NavigateAsync(new Uri($"{fixture.Access.Url("/")}#token={fixture.Token}"), cancellationToken);
        await chrome.WaitUntilAsync("document.getElementById('status').dataset.state", "connected", cancellationToken);
        await chrome.WaitUntilAsync("document.querySelector('section.machine .host-version')?.textContent", $"dsharp {RemoteHostServer.ReleaseVersion}", cancellationToken);

        Assert.DoesNotContain(fixture.Token, await chrome.EvaluateAsync("location.href", cancellationToken), StringComparison.Ordinal);
        Assert.Equal("none", await chrome.EvaluateAsync("getComputedStyle(document.getElementById('login')).display", cancellationToken));
        Assert.Equal("[]", await chrome.ProblemsAsync(cancellationToken));
        await chrome.ScreenshotAsync(Path.Combine(artifacts, "host-info.png"), cancellationToken);
    }

    private static Task NoHost() => Task.CompletedTask;

    private static async Task<HttpStatusCode> GetWithHostAsync(HttpClient client, BridgeFixture fixture, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Access.Url("/"));
        request.Headers.Host = host;
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    /** A host daemon (RemoteHostServer on its owner-only loopback endpoint, backed by the capability tests' fake backend) plus a bridge pointing at it. */
    private sealed class BridgeFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _daemonStop;
        private readonly Task _daemon;

        private BridgeFixture(string root, RemoteHostCapabilityTests.FakeBackend backend, CancellationTokenSource daemonStop, Task daemon, WebBridgeServer bridge)
        {
            Root = root;
            Backend = backend;
            _daemonStop = daemonStop;
            _daemon = daemon;
            Bridge = bridge;
            // The test reads the token where a user would: the owner-only file next to the bridge's port file.
            Token = LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(root)) ?? throw new InvalidOperationException("bridge issued no token");
        }

        public string Root { get; }

        public RemoteHostCapabilityTests.FakeBackend Backend { get; }

        public WebBridgeServer Bridge { get; }

        public string Token { get; }

        /** Host connections opened for WebSockets: the bridge's own status connection to its machine is not one of them. */
        public int RelayedConnections => Backend.OpenConnections - 1;

        public BridgeAccess Access => new(Bridge.Port, Token);

        public static async Task<BridgeFixture> StartAsync(CancellationToken cancellationToken, bool withDaemon = true)
        {
            // Socket paths are capped at 108 characters, so the daemon root is a shallow directory inside the repository.
            var root = TempTree.CreateSocketDirectory("wb");
            var backend = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(cancellationToken);
            var daemonStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var server = new RemoteHostServer(new RemoteHostServerOptions(), backend);
            var daemon = withDaemon
                ? Task.Run(() => RemoteHostListener.ServeLoopbackAsync(server, root, daemonStop.Token), cancellationToken)
                : Task.CompletedTask;
            if (withDaemon)
            {
                // A completed handshake proves the daemon serves; its own host connection must be gone before tests count connections.
                await using (var probe = await RemoteHostListener.ConnectAsync(root, cancellationToken))
                await using (await RemoteHostClient.ConnectAsync(probe, null, cancellationToken))
                    Assert.Equal(1, backend.OpenConnections);
                await WebSocketRpc.WaitForAsync(() => backend.OpenConnections == 0);
            }

            var bridge = WebBridgeServer.Start(new WebBridgeOptions(root, 0));
            if (withDaemon)
                await WebSocketRpc.WaitForAsync(() => backend.OpenConnections == 1);
            return new BridgeFixture(root, backend, daemonStop, daemon, bridge);
        }

        /** Stops the host daemon, which closes every host connection it serves. */
        public async Task StopDaemonAsync()
        {
            await _daemonStop.CancelAsync();
            try
            {
                await _daemon;
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync();
            await StopDaemonAsync();
            _daemonStop.Dispose();
            await Backend.DisposeAsync();
            TempTree.Delete(Root);
        }
    }
}
