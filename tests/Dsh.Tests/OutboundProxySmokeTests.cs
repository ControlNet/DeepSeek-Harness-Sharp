using System.Net;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Web;

namespace Dsh.Tests;

/** Explicit public-network smoke: real forwarding and default HTTPS SNI/certificate validation. */
public sealed class OutboundProxySmokeTests
{
    [Theory(Explicit = true)]
    [InlineData("http", "http://example.com/")]
    [InlineData("http", "https://example.com/")]
    [InlineData("socks5", "http://example.com/")]
    [InlineData("socks5", "https://example.com/")]
    public async Task RealWebFetch_PinsProxyTarget_AndPreservesTlsIdentity(string scheme, string target)
    {
        await using var server = new TestForwardProxy(socks: scheme == "socks5", forward: true);
        var proxy = new OutboundProxy();
        proxy.Configure($"{scheme}://127.0.0.1:{server.Port}", null);
        IReadOnlyList<PublicAddress>? addresses = null;
        var provider = new HttpFetchProvider(
            resolveAddresses: async (host, token) =>
                addresses = await WebFetchHttpNetwork.ResolvePublicAddresses(host, token),
            proxy: proxy);

        var result = await provider.Fetch(new WebFetchRequest(target), TestContext.Current.CancellationToken);

        AssertExampleDomain(result, target);
        var line = Assert.Single(server.Requests);
        var destination = line.StartsWith("SOCKS ", StringComparison.Ordinal) || line.StartsWith("CONNECT ", StringComparison.Ordinal)
            ? new Uri($"http://{line.Split(' ')[1]}").Host
            : new Uri(line.Split(' ')[1]).Host;
        Assert.True(IPAddress.TryParse(destination, out _), line);
        Assert.True(WebFetchHttpNetwork.IsPublicIpAddress(destination), line);
        Assert.NotNull(addresses);
        Assert.Contains(addresses, address => IPAddress.Parse(address.Address).Equals(IPAddress.Parse(destination)));
        if (scheme == "socks5")
            Assert.StartsWith("SOCKS ", line);
        else if (target.StartsWith("https:", StringComparison.Ordinal))
            Assert.StartsWith("CONNECT ", line);
        else
            Assert.StartsWith("GET http://", line);
    }

    // Run alone in a fresh process: HttpClient.DefaultProxy captures the process/system proxy settings.
    [Theory(Explicit = true)]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/")]
    public async Task RealWebFetch_SystemDefaultRoute_PreservesTlsIdentity(string target)
    {
        var proxy = new OutboundProxy();
        Assert.Null(proxy.Address);

        var provider = new HttpFetchProvider(proxy: proxy);
        var result = await provider.Fetch(new WebFetchRequest(target), TestContext.Current.CancellationToken);

        AssertExampleDomain(result, target);
    }

    [Fact(Explicit = true)]
    public async Task RealWebFetch_SystemDefaultProxy_ForwardsPublicHttpAndTlsInIsolatedProcess()
    {
        await using var server = new TestForwardProxy(forward: true);
        var child = await OutboundProxyProcess.RunAsync(
            $"{typeof(OutboundProxySmokeTests).FullName}.{nameof(RealWebFetch_SystemDefaultRoute_PreservesTlsIdentity)}",
            $"http://127.0.0.1:{server.Port}", TestContext.Current.CancellationToken);
        Assert.True(child.ExitCode == 0, "Isolated default-route HTTP/TLS smoke failed.");

        var requests = server.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        var get = Assert.Single(requests, line => line.StartsWith("GET ", StringComparison.Ordinal));
        var httpTarget = new Uri(get.Split(' ')[1]);
        Assert.Equal(Uri.UriSchemeHttp, httpTarget.Scheme);
        Assert.Equal(80, httpTarget.Port);
        Assert.Equal("/", httpTarget.PathAndQuery);
        Assert.True(IPAddress.TryParse(httpTarget.Host, out _), get);
        Assert.True(WebFetchHttpNetwork.IsPublicIpAddress(httpTarget.Host), get);
        var connect = Assert.Single(requests, line => line.StartsWith("CONNECT ", StringComparison.Ordinal));
        var tlsTarget = new Uri($"http://{connect.Split(' ')[1]}");
        Assert.Equal(443, tlsTarget.Port);
        Assert.True(IPAddress.TryParse(tlsTarget.Host, out _), connect);
        Assert.True(WebFetchHttpNetwork.IsPublicIpAddress(tlsTarget.Host), connect);
    }

    [Theory(Explicit = true)]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/")]
    public async Task RealWebFetch_Bypass_ConnectsDirectlyWithoutUsingConfiguredProxy(string target)
    {
        await using var server = new TestForwardProxy(forward: true);
        var proxy = new OutboundProxy();
        proxy.Configure($"http://127.0.0.1:{server.Port}", ["example.com"]);
        Assert.Null(proxy.CaptureRoute(new Uri(target)).ProxyUri);
        var provider = new HttpFetchProvider(proxy: proxy);

        var result = await provider.Fetch(new WebFetchRequest(target), TestContext.Current.CancellationToken);

        AssertExampleDomain(result, target);
        Assert.Empty(server.Requests);
        Assert.Equal(0, server.Connections);
    }

    private static void AssertExampleDomain(WebFetchResult result, string target)
    {
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(target, result.Url);
        Assert.Equal("html", result.Body.Kind);
        Assert.Contains("<title>Example Domain</title>", result.Body.Content, StringComparison.Ordinal);
        Assert.False(result.Truncated);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "LlmSmoke")]
    public async Task RealLlm_SavedProxyRoutesConfiguredProvider_AndCompletesTurn()
    {
        var configuredHome = HarnessHome.Resolve();
        var settings = HarnessSettings.Load(configuredHome);
        var model = (settings.GlobalDefaultModel ?? "").Split('/', 2);
        Assert.True(model.Length == 2, "Real smoke requires a configured default provider/model.");
        var providerId = model[0];
        var modelId = model[1];
        Assert.True(settings.Providers.ContainsKey(providerId), "Real smoke requires the configured default provider.");
        var directory = TempTree.CreateDirectory("outbound-proxy-real-llm");
        try
        {
            var home = new HarnessHome(directory);
            File.Copy(Path.Combine(configuredHome.Root, "settings.yaml"), Path.Combine(directory, "settings.yaml"));
            using var app = await HarnessComposer.Compose(new HarnessOptions(home, Cwd: Directory.GetCurrentDirectory(), Provider: providerId, Model: modelId));
            await using var server = new TestForwardProxy(forward: true);
            var facade = new Dsh.Gui.Services.SettingsFacade(app.Ctx, home);
            facade.UpdateOutboundProxy(new OutboundProxySettings { Url = $"http://127.0.0.1:{server.Port}" });
            var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var handle = await agents.Create(new CreateAgentOptions(SessionId.Create($"proxy-smoke-{Guid.NewGuid():N}"), Directory.GetCurrentDirectory(), new AgentOptions(providerId, modelId)), TestContext.Current.CancellationToken);
            var agent = (AgentLoopAgent)handle.Agent;
            agent.Followup(MessageFactory.CreateUserText("只回复两个字:收到"));
            await agent.WhenIdle().WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            Assert.Contains(agent.Session.SnapshotEvents().Select(item => item.Data).OfType<AssistantMessagePayload>(), item => item.Message.Content.Count > 0);
            Assert.Contains(server.Requests, line => line.StartsWith("CONNECT ", StringComparison.Ordinal));
            Assert.Equal($"http://127.0.0.1:{server.Port}", HarnessSettings.Load(home).Network?.OutboundProxy?.Url);
        }
        finally
        {
            TempTree.Delete(directory);
        }
    }
}
