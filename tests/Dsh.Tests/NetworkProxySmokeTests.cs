using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Runtime;
using Dsh.Transport;
using Dsh.Core;
using Dsh.Persistence;
using Dsh.RemoteHost;
using Dsh.Web;
using Dsh.Llm;

namespace Dsh.Tests;

/** Explicit public-network acceptance through real SSH streams and forwarding proxies, without model calls. */
public sealed class NetworkProxySmokeTests
{
    private const string HttpTarget = "http://example.com/";
    private const string HttpsTarget = "https://example.com/";
    private const string TlsMachineName = "tls-proxy";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory(Explicit = true, Timeout = 90000)]
    [InlineData("http", HttpTarget, 1)]
    [InlineData("http", HttpsTarget, 1)]
    [InlineData("http", HttpsTarget, 2)]
    [InlineData("socks5", HttpTarget, 1)]
    [InlineData("socks5", HttpsTarget, 1)]
    [InlineData("socks5", HttpsTarget, 2)]
    public async Task SshBorrowedProxy_ForwardsAuthenticatedPublicHttpAndTls(string scheme, string target, int version)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var root = TempTree.CreateSocketDirectory("n79s");
        try
        {
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(root, "run", "host.sock"));
            var user = $"u{Guid.NewGuid():N}";
            var password = $"p{Guid.NewGuid():N}";
            var credential = $"{user}:{password}";
            await using var forwarding = new TestForwardProxy(socks: scheme == "socks5", requiredCredential: credential, forward: true);
            var machine = ssh.Machine("public-proxy", root) with
            {
                OutboundTunnel = new("proxyCommand", $"{scheme}://{user}:{password}@127.0.0.1:{forwarding.Port}"),
            };
            await using var tunnel = new SshOutboundTunnel(machine);
            var commands = new ConcurrentQueue<string>();
            using var outbound = new OutboundProxy();
            outbound.ConfigureBorrowed(tunnel.ProxyUri.AbsoluteUri, (endpoint, token) => ConnectObservedAsync(tunnel, endpoint, token, commands));
            using var client = new HttpClient(outbound.CreateHandler(allowAutoRedirect: false));

            await AssertExampleDomainAsync(client, target, version, TestContext.Current.CancellationToken);

            AssertProxyRequest(forwarding, scheme, target);
            AssertCommandStreams(forwarding, commands.ToArray(), "127.0.0.1", forwarding.Port);
            Assert.True(forwarding.Credentials.Count > 0 && forwarding.Credentials.All(value => value == credential),
                "The real forwarding proxy must authenticate only this fixture's credential.");
            AssertPrivateOutput(outbound.Describe(), user, password);
        }
        finally
        {
            try
            {
                RealHostProcesses.KillAll(root);
            }
            finally
            {
                TempTree.Delete(root);
                guard.AssertUnchanged();
            }
        }
    }

    [Fact(Explicit = true, Timeout = 240000)]
    public async Task IrohBorrowedProxy_ForwardsPublicHttpAndTlsThroughTheBuiltInProxyCommand()
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var root = TempTree.CreateSocketDirectory("n79i");
        try
        {
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(root, "run", "host.sock"));
            var source = ssh.Machine("iroh-public-proxy", root);
            var receiver = IrohRemoteInstaller.ParseStatus(await new SshTransport(source).RunAsync(
                $"{RemoteShell.Prelude(source)}{RemoteShell.Quote(executable)} remote enable --ssh-port {source.Port}", Ct));
            Assert.True(receiver.Running, "The fixture's real iroh receiver must be running.");
            var user = $"u{Guid.NewGuid():N}";
            var password = $"p{Guid.NewGuid():N}";
            var credential = $"{user}:{password}";
            await using var forwarding = new TestForwardProxy(requiredCredential: credential, forward: true);
            var machine = source with
            {
                Kind = MachineKinds.Iroh,
                Host = "192.0.2.1",
                EndpointId = receiver.EndpointId,
                OutboundTunnel = new("proxyCommand", $"http://{user}:{password}@127.0.0.1:{forwarding.Port}"),
            };
            await using var tunnel = new SshOutboundTunnel(machine);
            var commands = new ConcurrentQueue<string>();
            using var outbound = new OutboundProxy();
            outbound.ConfigureBorrowed(tunnel.ProxyUri.AbsoluteUri, (endpoint, token) => ConnectObservedAsync(tunnel, endpoint, token, commands));
            using var client = new HttpClient(outbound.CreateHandler(allowAutoRedirect: false));

            await AssertExampleDomainAsync(client, HttpTarget, 1, TestContext.Current.CancellationToken);
            await AssertExampleDomainAsync(client, HttpsTarget, 2, TestContext.Current.CancellationToken);

            Assert.Equal(2, forwarding.Requests.Count);
            Assert.True(forwarding.Requests.Contains($"GET {HttpTarget} HTTP/1.1"), "The iroh route must carry a real absolute-form HTTP request.");
            Assert.True(forwarding.Requests.Contains("CONNECT example.com:443 HTTP/1.1"), "The iroh route must carry the real TLS CONNECT request.");
            AssertCommandStreams(forwarding, commands.ToArray(), "127.0.0.1", forwarding.Port);
            Assert.True(forwarding.Credentials.Count >= 2 && forwarding.Credentials.All(value => value == credential),
                "Both public requests must authenticate at this fixture's real forwarding proxy.");
            AssertPrivateOutput(outbound.Describe(), user, password);
        }
        finally
        {
            try
            {
                await IrohReceiver.DisableAsync(new HarnessHome(root), null, CancellationToken.None);
            }
            finally
            {
                try
                {
                    RealHostProcesses.KillAll(root);
                }
                finally
                {
                    TempTree.Delete(root);
                    guard.AssertUnchanged();
                }
            }
        }
    }

    [Fact(Explicit = true, Timeout = 120000)]
    public async Task HttpsBorrowedProxy_PreservesLocalhostCertificateIdentityInAnIsolatedProcess()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var root = TempTree.CreateSocketDirectory("n79t");
        try
        {
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(root, "run", "host.sock"));
            var user = $"u{Guid.NewGuid():N}";
            var password = $"p{Guid.NewGuid():N}";
            var credential = $"{user}:{password}";
            await using var forwarding = new TestForwardProxy(requiredCredential: credential, forward: true);
            await using var tls = await HttpsForwardProxyFixture.StartAsync(root, forwarding, Ct);
            var machine = ssh.Machine(TlsMachineName, root) with
            {
                OutboundTunnel = new("proxyCommand", $"https://{user}:{password}@localhost:{tls.Port}"),
            };
            new MachineStore(new HarnessHome(root)).Upsert(machine);

            var child = await OutboundProxyProcess.RunAsync(
                $"{typeof(NetworkProxySmokeTests).FullName}.{nameof(HttpsBorrowedProxy_ChildUsesFixtureTrustAndSavedMachine)}",
                null, Ct, certificateFile: tls.CertificateFile);

            AssertPrivateOutput(child.StandardOutput + child.StandardError, user, password);
            Assert.True(child.ExitCode == 0, "The isolated real HTTPS-proxy HTTP/TLS smoke failed.");
            var commands = await File.ReadAllLinesAsync(Path.Combine(root, "ssh-command-evidence.txt"), Ct);
            AssertCommandStreams(forwarding, commands, "localhost", tls.Port);
            Assert.Equal(2, forwarding.Requests.Count);
            Assert.True(forwarding.Requests.Contains($"GET {HttpTarget} HTTP/1.1"), "The HTTPS proxy must forward the public absolute-form HTTP request.");
            Assert.True(forwarding.Requests.Contains("CONNECT example.com:443 HTTP/1.1"), "The HTTPS proxy must forward the public TLS CONNECT request.");
            Assert.True(forwarding.Credentials.Count >= 2 && forwarding.Credentials.All(value => value == credential),
                "The TLS-wrapped real proxy must receive only this fixture's credential.");
        }
        finally
        {
            try
            {
                RealHostProcesses.KillAll(root);
            }
            finally
            {
                TempTree.Delete(root);
                guard.AssertUnchanged();
            }
        }
    }

    // SSL_CERT_FILE is an existing process-local technical acceptance input, not a product configuration variable.
    [Fact(Explicit = true, Timeout = 90000)]
    public async Task HttpsBorrowedProxy_ChildUsesFixtureTrustAndSavedMachine()
    {
        var certificateFile = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
        if (string.IsNullOrWhiteSpace(certificateFile) || !File.Exists(certificateFile))
        {
            Assert.Skip("This child requires the parent HTTPS-proxy fixture and its isolated CA file.");
            return;
        }
        var root = Path.GetDirectoryName(Path.GetFullPath(certificateFile))!;
        var home = new HarnessHome(root);
        if (!File.Exists(home.SettingsFile))
        {
            Assert.Skip("This child requires the parent's saved fixture machine next to its CA file.");
            return;
        }
        var machine = new MachineStore(home).Load().Machines.SingleOrDefault(value => value.Name == TlsMachineName);
        if (machine is null)
        {
            Assert.Skip("The parent HTTPS-proxy fixture machine is not present.");
            return;
        }
        await using var tunnel = new SshOutboundTunnel(machine);
        var commands = new ConcurrentQueue<string>();
        Assert.Equal(Uri.UriSchemeHttps, tunnel.ProxyUri.Scheme);
        Assert.Equal("localhost", tunnel.ProxyUri.Host);
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.AbsoluteUri, (endpoint, token) => ConnectObservedAsync(tunnel, endpoint, token, commands));
        using var client = new HttpClient(outbound.CreateHandler(allowAutoRedirect: false));

        await AssertExampleDomainAsync(client, HttpTarget, 1, TestContext.Current.CancellationToken);
        await AssertExampleDomainAsync(client, HttpsTarget, 2, TestContext.Current.CancellationToken);
        OwnerOnlyFiles.Write(Path.Combine(root, "ssh-command-evidence.txt"), string.Join(Environment.NewLine, commands));
    }

    [Theory(Explicit = true, Timeout = 240000)]
    [InlineData("A")]
    [InlineData("A with spaces")]
    public async Task RealTuiSelection_PersistsTheSharedRoute_ForAnActualHttpFetchConsumer(string selectedMachine)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var selected = new TestForwardProxy(forward: true);
        await using var other = new TestForwardProxy(forward: true);
        await using var baseline = new TestForwardProxy(forward: true);
        var root = RealHostProcesses.CreateHome("b80t");
        try
        {
            var home = PrepareTuiHome(root, ssh, selected, other, baseline);
            var store = new MachineStore(home);
            store.Upsert(store.Load().Find("A")! with { Name = selectedMachine });
            var before = HarnessSettings.Load(home).Network!.OutboundProxy!.Url;
            using var terminal = new PtyHost();
            var session = await StartTuiAsync(terminal, executable, root);
            await WaitUntilAsync(() => TuiReady(session), "The real TUI must render its input before commands are sent.");
            await session.WriteAsync(Encoding.UTF8.GetBytes($"/proxy via {selectedMachine}\r"), Ct);
            await WaitUntilAsync(() => HarnessSettings.Load(home).Network?.BorrowedOutboundMachine == selectedMachine, "The real TUI must persist its validated selection.");

            using (var outbound = new OutboundProxy())
            {
                await using var route = new BorrowedOutboundRoute(home, outbound);
                await using var lease = await route.AcquireAsync(Ct);
                var result = await new HttpFetchProvider(proxy: outbound).Fetch(new WebFetchRequest(HttpsTarget), Ct);
                Assert.Equal(200, result.StatusCode);
                Assert.Contains("Example Domain", result.Body.Content);
            }
            Assert.Contains(selected.Requests, request => request.StartsWith("CONNECT ", StringComparison.Ordinal));
            Assert.Empty(other.Requests);
            Assert.Equal(before, HarnessSettings.Load(home).Network?.OutboundProxy?.Url);
            var saved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            await session.WriteAsync(Encoding.UTF8.GetBytes("/proxy via missing-machine\r"), Ct);
            await WaitUntilAsync(() => TuiText(session, out _).Contains("selected outbound machine does not exist", StringComparison.Ordinal),
                "An invalid TUI selection must produce an explicit command error.");
            Assert.True(saved == await File.ReadAllTextAsync(home.SettingsFile, Ct), "An invalid TUI selection must leave settings unchanged.");
            await session.WriteAsync(Encoding.UTF8.GetBytes("/proxy direct\r"), Ct);
            await WaitUntilAsync(() => HarnessSettings.Load(home).Network?.BorrowedOutboundMachine is null, "Direct must clear only the borrowed selection.");
            await session.WriteAsync(Encoding.UTF8.GetBytes("/exit\r"), Ct);
            await session.WaitForExitAsync(Ct);
            Assert.Equal(0, session.ExitCode);
            await BorrowedOutboundProxyTests.AssertOwnedSshRetiredAsync(root, selected.Port);
            Assert.Empty(other.Requests);
        }
        finally
        {
            RealHostProcesses.KillAll(root);
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true, Timeout = 360000)]
    [Trait("Category", "LlmSmoke")]
    public async Task RealTuiModel_WebFetchAndSearch_AndLocalHostReload_UseTheSameSavedBorrowedSelection()
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var selected = new TestForwardProxy(forward: true);
        await using var other = new TestForwardProxy(forward: true);
        await using var baseline = new TestForwardProxy(forward: true);
        var root = RealHostProcesses.CreateHome("b80m");
        try
        {
            RealHostProcesses.CopyRealConfiguration(root);
            var home = new HarnessHome(root);
            var settings = HarnessSettings.Load(home);
            var model = settings.ResolveDefaultModel() ?? throw new InvalidOperationException("The real TUI smoke needs a selected HTTP provider and model.");
            var provider = settings.Providers[model.Provider];
            var options = provider.Options ?? throw new InvalidOperationException("The selected provider needs HTTP options.");
            var credential = options.ApiKey ?? (options.ApiKeyEnv is { } name ? new EnvCredentials(HarnessHome.Default()).Get(name) : null);
            Assert.False(string.IsNullOrWhiteSpace(credential), "The real TUI smoke needs its selected provider's actual credential.");
            options.ApiKey = credential;
            options.ApiKeyEnv = null;
            var providerHost = new Uri(options.BaseUrl!).Host;
            settings.Providers = new() { [model.Provider] = provider };
            settings.Fleet = null;
            settings.Plugins.Remove("@deepseek-ai/dsh-fleet");
            settings.Plugins.Remove("@deepseek-ai/dsh-web-search-github");
            settings.Save(home);
            settings.SavePlugins(home);
            PrepareTuiHome(root, ssh, selected, other, baseline);
            using var hostProcess = RealHostProcesses.StartHost(executable, root);
            await using var host = await RemoteHostClient.ConnectAsync(await RemoteHostListener.ConnectAsync(root, Ct,
                startupLifetime: hostProcess.WaitForExitAsync(Ct)), null, "borrowed-route-acceptance", Ct);
            using var terminal = new PtyHost();
            var tui = await StartTuiAsync(terminal, executable, root);
            await WaitUntilAsync(() => TuiReady(tui), "The real model TUI must become ready.");
            await tui.WriteAsync(Encoding.UTF8.GetBytes("/proxy via A\r"), Ct);
            await WaitUntilAsync(() => HarnessSettings.Load(home).Network?.BorrowedOutboundMachine == "A", "The real TUI must select A.");
            await tui.WriteAsync(Encoding.UTF8.GetBytes("Use web_fetch to read https://example.com/ and web_search with engine github-repos to find the official dotnet runtime repository, then summarize both results.\r"), Ct);
            IReadOnlyList<SessionEvent>? completed = null;
            await WaitUntilAsync(() => (completed = ReadCompletedTuiTurn(home)) is not null, "The real TUI model turn must complete.");
            Assert.NotNull(completed);
            Assert.IsType<TurnEndReason.Completed>(completed.Select(value => value.Data).OfType<TurnEndPayload>().Last().Reason);
            var calls = completed.Select(value => value.Data).OfType<ToolCallPayload>().ToArray();
            Assert.Contains(calls, call => call.Name == "web_fetch");
            var searchCalls = calls.Where(call => call.Name == "web_search").Select(call => call.CallId).ToHashSet();
            var results = completed.Select(value => value.Data).OfType<ToolResultPayload>().ToArray();
            Assert.Contains(results, result => result.Error is null && result.Message.Block.Content.OfType<TextBlock>()
                .Any(block => block.Text.Contains("Example Domain", StringComparison.Ordinal)));
            Assert.Contains(results, result => WebFetchTool.FetchMetaFromResult(result.Meta) is { StatusCode: 200, Url: HttpsTarget });
            var sources = results.Where(result => result.Error is null && searchCalls.Contains(result.Message.Block.ToolCallId))
                .SelectMany(result => WebSearchTool.SearchMetaFromResult(result.Meta)?.Sources ?? []).ToArray();
            Assert.NotEmpty(sources);
            Assert.Contains(selected.Requests, request => request.StartsWith($"CONNECT {providerHost}:443 ", StringComparison.Ordinal));
            Assert.Contains(selected.Requests, request => request.StartsWith("CONNECT api.github.com:443 ", StringComparison.Ordinal));
            using (var outbound = new OutboundProxy())
            {
                await using var route = new BorrowedOutboundRoute(home, outbound);
                await using var lease = await route.AcquireAsync(Ct);
                using var client = new HttpClient(outbound.CreateHandler());
                using var source = await client.GetAsync(sources[0].Url, HttpCompletionOption.ResponseHeadersRead, Ct);
                Assert.Equal(HttpStatusCode.OK, source.StatusCode);
            }

            var beforeHost = selected.Requests.Count;
            await host.Network.ReloadAsync(Ct);
            var hostSession = await host.CreateSessionAsync(root, new RemoteModelSelection(model.Provider, model.Model), cancellationToken: Ct);
            var hostEvents = ReadHostTurnAsync(host, hostSession.Id);
            await host.SendMessageAsync(hostSession.Id, "Use web_fetch to read https://example.com/ and summarize the page.", [], Ct);
            var events = await hostEvents;
            Assert.IsType<TurnEndReason.Completed>(Assert.IsType<TurnEndPayload>(events[^1].Data).Reason);
            Assert.Contains(events.Select(value => value.Data).OfType<ToolCallPayload>(), call => call.Name == "web_fetch");
            Assert.Contains(events.Select(value => value.Data).OfType<ToolResultPayload>(), result => result.Error is null
                && result.Message.Block.Content.OfType<TextBlock>().Any(block => block.Text.Contains("Example Domain", StringComparison.Ordinal)));
            Assert.True(selected.Requests.Count > beforeHost);
            Assert.Empty(other.Requests);
            await tui.WriteAsync(Encoding.UTF8.GetBytes("/exit\r"), Ct);
            await tui.WaitForExitAsync(Ct);
            Assert.Equal(0, tui.ExitCode);
            Assert.True(await host.PingAsync(Ct) > 0, "Exiting TUI must leave the independent local Host alive.");
            await host.Network.ReloadAsync(Ct);
            HarnessSettings.SaveBorrowedOutboundMachine(home, null);
            await host.Network.ReloadAsync(Ct);
            await BorrowedOutboundProxyTests.AssertOwnedSshRetiredAsync(root, selected.Port);
        }
        finally
        {
            RealHostProcesses.KillAll(root);
            TempTree.Delete(root);
        }
    }

    private static HarnessHome PrepareTuiHome(string root, TestSshServer ssh, TestForwardProxy selected, TestForwardProxy other, TestForwardProxy baseline)
    {
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(root, "run", "host.sock"));
        var home = new HarnessHome(root);
        var settings = HarnessSettings.Load(home);
        settings.Network = new NetworkSettings { OutboundProxy = new OutboundProxySettings { Url = $"http://127.0.0.1:{baseline.Port}", ProbeUrl = HttpsTarget } };
        settings.Fleet = null;
        settings.Save(home);
        var store = new MachineStore(home);
        store.Upsert(BorrowedOutboundProxyTests.ObserveSsh(ssh.Machine("A", root), root) with { OutboundTunnel = new("proxyCommand", $"http://127.0.0.1:{selected.Port}") });
        store.Upsert(ssh.Machine("B", root) with { OutboundTunnel = new("proxyCommand", $"http://127.0.0.1:{other.Port}") });
        return home;
    }

    private static Task<PtySession> StartTuiAsync(PtyHost host, string executable, string root)
        => host.StartAsync(new PtyStartInfo
        {
            FileName = executable,
            Arguments = ["tui"],
            Home = root,
            WorkingDirectory = root,
            Columns = 140,
            Rows = 40,
            Environment = new Dictionary<string, string?>
            {
                [HarnessHome.HomeEnv] = root,
                [HarnessHome.ChildHomeEnv] = root,
                ["DSH_PTY_CHILD"] = "1",
                ["TERM"] = "xterm-256color",
                ["LC_ALL"] = "C",
            },
        }, cancellationToken: Ct);

    private static bool TuiReady(PtySession session)
    {
        var text = TuiText(session, out var alternateScreen);
        return alternateScreen && text.Contains("快捷键", StringComparison.Ordinal);
    }

    private static string TuiText(PtySession session, out bool alternateScreen)
    {
        var screen = new VtScreen(140, 40);
        screen.Feed(session.Snapshot());
        alternateScreen = screen.IsAlternateScreen;
        var text = new StringBuilder(140 * 41);
        for (var row = 0; row < screen.Height; row++)
        {
            foreach (var cell in screen.Row(row))
                if (cell.Character != '\0')
                    text.Append(cell.Character);
            text.Append('\n');
        }
        return text.ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        while (!condition())
        {
            Assert.False(deadline.IsCancellationRequested, failure);
            await Task.Delay(50, Ct);
        }
    }

    private static IReadOnlyList<SessionEvent>? ReadCompletedTuiTurn(HarnessHome home)
    {
        using var persistence = new JsonlSessionPersistence(home.SessionsPath);
        foreach (var session in persistence.List())
        {
            using var handle = persistence.Open(session.Header.Id, SessionAccess.Read);
            var events = handle.Read();
            if (events.Any(value => value.Data is TurnEndPayload))
                return events;
        }
        return null;
    }

    private static async Task<IReadOnlyList<SessionEvent>> ReadHostTurnAsync(RemoteHostClient host, string sessionId)
    {
        var events = new List<SessionEvent>();
        await foreach (var item in host.SubscribeAsync(sessionId, 0, Ct))
        {
            var value = DshJson.Deserialize<SessionEvent>(item.EventJson)!;
            events.Add(value);
            if (value.Data is TurnEndPayload)
                return events;
        }
        throw new IOException("The Host event stream ended before its real model turn completed.");
    }

    private static async ValueTask<Stream> ConnectObservedAsync(SshOutboundTunnel tunnel, DnsEndPoint endpoint,
        CancellationToken token, ConcurrentQueue<string> commands)
    {
        var stream = (SshProxyStream)await tunnel.ConnectAsync(endpoint, token);
        var arguments = stream.Process.StartInfo.ArgumentList;
        var forwarding = arguments.IndexOf("-W");
        if (forwarding < 0 || forwarding + 1 >= arguments.Count)
        {
            await stream.DisposeAsync();
            throw new InvalidOperationException("The actual SSH process has no stdio forwarding destination.");
        }
        commands.Enqueue(arguments[forwarding + 1]);
        return stream;
    }

    private static void AssertCommandStreams(TestForwardProxy forwarding, string[] commands, string host, int port)
    {
        Assert.Equal(forwarding.Connections, commands.Length);
        Assert.All(commands, target => Assert.Equal($"{host}:{port}", target));
    }

    private static async Task AssertExampleDomainAsync(HttpClient client, string target, int version, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target)
        {
            Version = version == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(request.Version, response.Version);
        Assert.Equal(target, response.RequestMessage!.RequestUri!.AbsoluteUri);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("<title>Example Domain</title>", body, StringComparison.Ordinal);
    }

    private static void AssertProxyRequest(TestForwardProxy forwarding, string scheme, string target)
    {
        var expected = scheme == "socks5"
            ? $"SOCKS example.com:{new Uri(target).Port}"
            : target == HttpsTarget ? "CONNECT example.com:443 HTTP/1.1" : $"GET {target} HTTP/1.1";
        Assert.True(forwarding.Requests.Count == 1 && forwarding.Requests.TryPeek(out var actual) && actual == expected,
            "The real forwarding proxy must record this request's expected protocol and public destination.");
    }

    private static void AssertPrivateOutput(string output, string user, string password)
    {
        var credential = $"{user}:{password}";
        Assert.True(!output.Contains(user, StringComparison.Ordinal) && !output.Contains(password, StringComparison.Ordinal)
            && !output.Contains(Uri.EscapeDataString(credential), StringComparison.Ordinal)
            && !output.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(credential)), StringComparison.Ordinal)
            && !output.Contains("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase),
            "Diagnostics must not expose fixture credentials, their encoded forms or proxy authentication headers.");
    }
}
