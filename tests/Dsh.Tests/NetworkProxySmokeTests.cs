using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Runtime;
using Dsh.Transport;

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
