using System.Net;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Web;

namespace Dsh.Tests;

/** 显式网络 smoke: 本地代理真实转发, HTTPS 保持默认 SNI/证书校验, 不使用固定响应。 */
public sealed class OutboundProxySmokeTests
{
    [Theory(Explicit = true)]
    [InlineData("http", "http://example.com/")]
    [InlineData("http", "https://example.com/")]
    [InlineData("socks5", "https://example.com/")]
    public async Task RealWebFetch_PinsProxyTarget_AndPreservesTlsIdentity(string scheme, string target)
    {
        await using var server = new TestForwardProxy(socks: scheme == "socks5", forward: true);
        var proxy = new OutboundProxy();
        proxy.Configure($"{scheme}://127.0.0.1:{server.Port}", null);
        var provider = new HttpFetchProvider(proxy: proxy);

        var result = await provider.Fetch(new WebFetchRequest(target), TestContext.Current.CancellationToken);

        Assert.Equal(200, result.StatusCode);
        Assert.Contains("Example Domain", result.Body.Content);
        var line = Assert.Single(server.Requests);
        var destination = line.StartsWith("SOCKS ", StringComparison.Ordinal) || line.StartsWith("CONNECT ", StringComparison.Ordinal)
            ? new Uri($"http://{line.Split(' ')[1]}").Host
            : new Uri(line.Split(' ')[1]).Host;
        Assert.True(IPAddress.TryParse(destination, out _), line);
        Assert.True(WebFetchHttpNetwork.IsPublicIpAddress(destination), line);
        Assert.Equal(target, result.Url);
    }

    [Fact(Explicit = true)]
    public async Task RealLlm_ExistingClientSwitchesToSavedProxy_AndCompletesTurn()
    {
        var configuredHome = HarnessHome.Resolve();
        var settings = HarnessSettings.Load(configuredHome);
        const string providerId = "deepseek-official";
        const string modelId = "deepseek-v4-flash";
        Assert.True(settings.Providers.TryGetValue(providerId, out var provider) && !string.IsNullOrWhiteSpace(provider.Options?.ApiKey), "Real smoke requires the configured deepseek-official provider.");
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
