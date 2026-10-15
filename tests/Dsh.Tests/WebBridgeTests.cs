using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
        using var socket = await fixture.ConnectWebSocketAsync(await fixture.LoginAsync(cancellationToken), fixture.Origin, cancellationToken);

        var info = await RequestAsync(socket, 1, HostProtocol.MethodInfo, null);
        Assert.Equal(HostProtocol.Version, info.GetProperty("ProtocolVersion").GetInt32());
        Assert.Equal("fake", info.GetProperty("HostVersion").GetString());

        var created = await RequestAsync(socket, 2, HostProtocol.MethodSessionCreate, """{"Cwd":"/work"}""");
        var sessionId = created.GetProperty("Id").GetString();
        await RequestAsync(socket, 3, HostProtocol.MethodSessionSubscribe, $$"""{"SessionId":"{{sessionId}}","FromSeq":0}""");
        await SendAsync(socket, 4, HostProtocol.MethodSessionMessage, $$"""{"SessionId":"{{sessionId}}","Text":"hi"}""");
        var pushed = await ReceiveUntilAsync(socket, message => message.TryGetProperty("method", out var method) && method.GetString() == HostProtocol.NotificationEvent);

        Assert.False(pushed.TryGetProperty("id", out _));
        Assert.Equal(sessionId, pushed.GetProperty("params").GetProperty("SessionId").GetString());
        Assert.Equal("user:hi", pushed.GetProperty("params").GetProperty("EventJson").GetString());
    }

    [Fact(Timeout = 30000)]
    public async Task WebSocket_WithoutSessionCookie_IsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = BridgeFixture.CreateWebSocket(new CookieContainer(), fixture.Origin);

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(fixture.WebSocketUri, cancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, socket.HttpStatusCode);
        Assert.Equal(0, fixture.Backend.OpenConnections);
    }

    [Fact(Timeout = 30000)]
    public async Task WebSocket_FromForeignOrigin_IsRefusedEvenWithSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = BridgeFixture.CreateWebSocket(await fixture.LoginAsync(cancellationToken), ForeignOrigin);

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(fixture.WebSocketUri, cancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, socket.HttpStatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task Login_ExchangesTokenForStrictHttpOnlySessionCookie()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        var cookies = new CookieContainer();
        using var client = BridgeFixture.CreateHttpClient(cookies);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(fixture.Url(WebBridgeServer.SessionPath), cancellationToken)).StatusCode);

        using var wrongToken = await fixture.PostLoginAsync(client, "0123", fixture.Origin, cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        using var crossSite = await fixture.PostLoginAsync(client, fixture.Token, ForeignOrigin, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Empty(cookies.GetAllCookies());

        using var accepted = await fixture.PostLoginAsync(client, fixture.Token, fixture.Origin, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        var setCookie = Assert.Single(accepted.Headers.GetValues("Set-Cookie"));
        Assert.Contains("HttpOnly", setCookie, StringComparison.Ordinal);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Token, setCookie, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(fixture.Url(WebBridgeServer.SessionPath), cancellationToken)).StatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task HostHeader_MustNameLoopback_ButMayCarryAForwardedPort()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var client = BridgeFixture.CreateHttpClient(new CookieContainer());

        Assert.Equal(HttpStatusCode.Forbidden, await GetWithHostAsync(client, fixture, $"evil.example:{fixture.Bridge.Port}"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetWithHostAsync(client, fixture, $"127.0.0.1.evil.example:{fixture.Bridge.Port}"));
        Assert.Equal(HttpStatusCode.OK, await GetWithHostAsync(client, fixture, "localhost:9999"));
    }

    [Fact(Timeout = 30000)]
    public async Task FrontEnd_IsServedFromEmbeddedAssetsWithSecurityHeaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var client = BridgeFixture.CreateHttpClient(new CookieContainer());

        using var page = await client.GetAsync(fixture.Url("/"), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("frame-ancestors 'none'", Assert.Single(page.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.Contains("app.js", await page.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);

        using var script = await client.GetAsync(fixture.Url("/rpc.js"), cancellationToken);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(fixture.Url("/missing.js"), cancellationToken)).StatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task ClosingWebSocket_ReleasesItsHostConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = await fixture.ConnectWebSocketAsync(await fixture.LoginAsync(cancellationToken), fixture.Origin, cancellationToken);
        await RequestAsync(socket, 1, HostProtocol.MethodInfo, null);
        Assert.Equal(1, fixture.Backend.OpenConnections);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);

        await WaitForAsync(() => fixture.Backend.OpenConnections == 0);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact(Timeout = 30000)]
    public async Task HostShutdown_ClosesWebSocketAsEndpointUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = await fixture.ConnectWebSocketAsync(await fixture.LoginAsync(cancellationToken), fixture.Origin, cancellationToken);
        await RequestAsync(socket, 1, HostProtocol.MethodInfo, null);

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
        using var socket = await fixture.ConnectWebSocketAsync(await fixture.LoginAsync(cancellationToken), fixture.Origin, cancellationToken);

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
            await WaitForAsync(() => File.Exists(WebBridgeEndpoint.TokenFile(root)) && output.ToString().Contains("ssh -N -L", StringComparison.Ordinal));

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

    /** Opens the real page in headless Chrome with the one-time #token link and checks that it logs in, connects and renders host.info. */
    [Fact(Timeout = 120000)]
    public async Task HeadlessChrome_LogsInThroughFragmentAndShowsHostInfo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        var artifacts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/web-bridge"));
        Directory.CreateDirectory(artifacts);
        await using var chrome = await HeadlessChrome.StartAsync(artifacts, cancellationToken);

        await chrome.SendAsync("Page.navigate", $$"""{"url":"{{fixture.Url("/")}}#token={{fixture.Token}}"}""", cancellationToken);
        await chrome.WaitUntilAsync("document.getElementById('status').dataset.state", "connected", cancellationToken);

        Assert.Equal("fake", await chrome.EvaluateAsync("document.querySelectorAll('#info dd')[1].textContent", cancellationToken));
        Assert.Equal("", await chrome.EvaluateAsync("location.hash", cancellationToken));
        Assert.Equal("none", await chrome.EvaluateAsync("getComputedStyle(document.getElementById('login')).display", cancellationToken));
        var screenshot = await chrome.SendAsync("Page.captureScreenshot", """{"format":"png"}""", cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(artifacts, "host-info.png"), Convert.FromBase64String(screenshot.GetProperty("data").GetString()!), cancellationToken);
    }

    private static Task NoHost() => Task.CompletedTask;

    private static async Task<HttpStatusCode> GetWithHostAsync(HttpClient client, BridgeFixture fixture, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Url("/"));
        request.Headers.Host = host;
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private static async Task SendAsync(ClientWebSocket socket, int id, string method, string? parameters)
    {
        var message = parameters is null
            ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""";
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, Ct);
    }

    private static async Task<JsonElement> RequestAsync(ClientWebSocket socket, int id, string method, string? parameters)
    {
        await SendAsync(socket, id, method, parameters);
        var response = await ReceiveUntilAsync(socket, message => message.TryGetProperty("id", out var reply) && reply.ValueKind == JsonValueKind.Number && reply.GetInt32() == id);
        Assert.False(response.TryGetProperty("error", out var error), error.ToString());
        return response.GetProperty("result");
    }

    /** Reads whole WebSocket text messages, each one JSON-RPC message, until one matches. */
    private static async Task<JsonElement> ReceiveUntilAsync(ClientWebSocket socket, Func<JsonElement, bool> match)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), Ct);
                Assert.Equal(WebSocketMessageType.Text, received.MessageType);
                message.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);

            var element = JsonDocument.Parse(message.ToArray()).RootElement.Clone();
            if (match(element))
                return element;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(10, Ct);
        Assert.True(condition(), "condition not met in time");
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

        public string Origin => $"http://127.0.0.1:{Bridge.Port}";

        public Uri WebSocketUri => new($"ws://127.0.0.1:{Bridge.Port}{WebBridgeServer.WebSocketPath}");

        public Uri Url(string path) => new($"{Origin}{path}");

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
                await WaitForAsync(() => backend.OpenConnections == 0);
            }

            return new BridgeFixture(root, backend, daemonStop, daemon, WebBridgeServer.Start(new WebBridgeOptions(root, 0)));
        }

        public static HttpClient CreateHttpClient(CookieContainer cookies)
            => new(new SocketsHttpHandler { CookieContainer = cookies, UseCookies = true });

        public static ClientWebSocket CreateWebSocket(CookieContainer cookies, string origin)
        {
            var socket = new ClientWebSocket();
            socket.Options.Cookies = cookies;
            socket.Options.SetRequestHeader("Origin", origin);
            socket.Options.CollectHttpResponseDetails = true;
            return socket;
        }

        public async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string token, string origin, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url(WebBridgeServer.LoginPath))
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]),
            };
            request.Headers.Add("Origin", origin);
            return await client.SendAsync(request, cancellationToken);
        }

        /** Logs in like the page does and returns the cookie jar holding the session cookie. */
        public async Task<CookieContainer> LoginAsync(CancellationToken cancellationToken)
        {
            var cookies = new CookieContainer();
            using var client = CreateHttpClient(cookies);
            using var response = await PostLoginAsync(client, Token, Origin, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            return cookies;
        }

        public async Task<ClientWebSocket> ConnectWebSocketAsync(CookieContainer cookies, string origin, CancellationToken cancellationToken)
        {
            var socket = CreateWebSocket(cookies, origin);
            await socket.ConnectAsync(WebSocketUri, cancellationToken);
            return socket;
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

    /** The machine's google-chrome, headless with a throwaway profile inside artifacts020, driven over the DevTools protocol; skips the test when Chrome is not installed. */
    private sealed class HeadlessChrome : IAsyncDisposable
    {
        private const string ListeningPrefix = "DevTools listening on ";

        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        private readonly Process _process;
        private readonly string _profile;
        private readonly ClientWebSocket _page = new();
        private int _nextId;

        private HeadlessChrome(Process process, string profile)
        {
            _process = process;
            _profile = profile;
        }

        public static async Task<HeadlessChrome> StartAsync(string artifacts, CancellationToken cancellationToken)
        {
            var profile = Path.Combine(artifacts, $"chrome-profile-{Guid.NewGuid():N}");
            var start = new ProcessStartInfo("google-chrome") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in (string[])["--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", $"--user-data-dir={profile}", "--remote-debugging-port=0", "--window-size=420,820", "about:blank"])
                start.ArgumentList.Add(argument);
            var chrome = new HeadlessChrome(StartOrSkip(start), profile);
            var browserEndpoint = await chrome.ReadDevToolsEndpointAsync(cancellationToken);
            await chrome.AttachToPageAsync(browserEndpoint, cancellationToken);
            return chrome;
        }

        /** Sends one DevTools command to the page and returns its result. */
        public async Task<JsonElement> SendAsync(string method, string parameters, CancellationToken cancellationToken)
        {
            var id = ++_nextId;
            await _page.SendAsync(Encoding.UTF8.GetBytes($$"""{"id":{{id}},"method":"{{method}}","params":{{parameters}}}"""), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            var reply = await ReceiveUntilAsync(_page, message => message.TryGetProperty("id", out var replyId) && replyId.GetInt32() == id);
            Assert.False(reply.TryGetProperty("error", out var error), error.ToString());
            return reply.GetProperty("result");
        }

        public async Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken)
        {
            var result = await SendAsync("Runtime.evaluate", $$"""{"expression":"{{JsonEncodedText.Encode(expression)}}","returnByValue":true}""", cancellationToken);
            return result.GetProperty("result").TryGetProperty("value", out var value) ? value.ToString() : "";
        }

        public async Task WaitUntilAsync(string expression, string expected, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + StartTimeout;
            var actual = await EvaluateAsync(expression, cancellationToken);
            while (actual != expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, cancellationToken);
                actual = await EvaluateAsync(expression, cancellationToken);
            }

            Assert.True(actual == expected, $"{expression} stayed \"{actual}\"; page status: {await EvaluateAsync("document.getElementById('status').textContent", cancellationToken)}");
        }

        /** Chrome prints the browser's DevTools WebSocket address on stderr once it listens; the rest of both streams is drained so Chrome never blocks on a full pipe. */
        private async Task<Uri> ReadDevToolsEndpointAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartTimeout);
            var line = await _process.StandardError.ReadLineAsync(timeout.Token);
            while (line is not null && !line.StartsWith(ListeningPrefix, StringComparison.Ordinal))
                line = await _process.StandardError.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            _ = _process.StandardError.ReadToEndAsync(CancellationToken.None);
            _ = _process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            return new Uri(line[ListeningPrefix.Length..].Trim());
        }

        private async Task AttachToPageAsync(Uri browserEndpoint, CancellationToken cancellationToken)
        {
            using var http = new HttpClient();
            var targets = JsonDocument.Parse(await http.GetStringAsync(new Uri($"http://{browserEndpoint.Authority}/json/list"), cancellationToken)).RootElement;
            var page = targets.EnumerateArray().First(target => target.GetProperty("type").GetString() == "page");
            await _page.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), cancellationToken);
        }

        private static Process StartOrSkip(ProcessStartInfo start)
        {
            try
            {
                return Process.Start(start) ?? throw new InvalidOperationException("google-chrome did not start");
            }
            catch (Win32Exception)
            {
                Assert.Skip("google-chrome is not installed on this machine");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _page.Dispose();
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            _process.Dispose();
            TempTree.Delete(_profile);
        }
    }
}
