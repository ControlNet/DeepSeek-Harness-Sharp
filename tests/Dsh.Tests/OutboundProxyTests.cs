using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/**
 * 本机出口代理(settings.yaml 的 network.outbound_proxy)端到端验证: 经真实组合根装配, 走真实 web_fetch 工具、/proxy 命令、自检与设置写回。
 * TestForwardProxy 是测试专用的本地代理替身: 只记录收到的请求并回固定响应, 不向外转发, 因此目标域名用不可解析的 .test。
 */
public sealed class OutboundProxyTests
{
    private const string FetchTarget = "http://example.test/page";
    private const string Password = "s:cr@t";
    private const string EscapedPassword = "s%3Acr%40t";

    [Fact]
    public async Task WebFetch_GoesThroughConfiguredHttpProxy()
    {
        await using var proxy = new TestForwardProxy();
        using var harness = await TestHarness.ComposeAsync($"url: http://127.0.0.1:{proxy.Port}");

        var result = await harness.FetchAsync(FetchTarget);

        Assert.False(result.IsError, Text(result));
        Assert.Contains(TestForwardProxy.ProxiedBody, Text(result));
        Assert.Equal([$"GET {FetchTarget} HTTP/1.1"], proxy.Requests);
    }

    [Fact]
    public async Task WebFetch_Https_OpensConnectTunnelThroughProxy()
    {
        await using var proxy = new TestForwardProxy();
        using var harness = await TestHarness.ComposeAsync($"url: 127.0.0.1:{proxy.Port}");

        var result = await harness.FetchAsync("https://example.test/");

        Assert.True(result.IsError);
        Assert.Contains(proxy.Requests, line => line.StartsWith("CONNECT example.test:443 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WebFetch_HttpProxyCredentials_AreSentAndRedacted()
    {
        await using var proxy = new TestForwardProxy(requiredCredential: $"alice:{Password}");
        using var harness = await TestHarness.ComposeAsync($"url: http://alice:{EscapedPassword}@127.0.0.1:{proxy.Port}");

        var result = await harness.FetchAsync(FetchTarget);

        Assert.False(result.IsError, Text(result));
        Assert.Equal([$"alice:{Password}"], proxy.Credentials);
        Assert.Equal($"http://***@127.0.0.1:{proxy.Port}", OutboundProxy.FromContext(harness.App.Ctx).Describe());
    }

    [Fact]
    public async Task WebFetch_Socks5WithCredentials_ResolvesTargetAtProxy()
    {
        await using var proxy = new TestForwardProxy(socks: true);
        using var harness = await TestHarness.ComposeAsync($"url: socks5://alice:{EscapedPassword}@127.0.0.1:{proxy.Port}");

        var result = await harness.FetchAsync(FetchTarget);

        Assert.False(result.IsError, Text(result));
        Assert.Contains(TestForwardProxy.ProxiedBody, Text(result));
        Assert.Equal([$"alice:{Password}"], proxy.Credentials);
        Assert.Equal(["SOCKS example.test:80", "GET /page HTTP/1.1"], proxy.Requests);
    }

    /** 未配置(跟随系统代理)或目标在 bypass 里时, 请求不得经过配置之外的这个代理。 */
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebFetch_WithoutProxyOrBypassed_DoesNotTouchTestProxy(bool bypassed)
    {
        await using var proxy = new TestForwardProxy();
        var network = bypassed ? $"url: http://127.0.0.1:{proxy.Port}\n    bypass: [example.test]" : null;
        using var harness = await TestHarness.ComposeAsync(network);

        await harness.FetchAsync(FetchTarget);

        Assert.Equal(0, proxy.Connections);
        Assert.NotEqual(proxy.Port, OutboundProxy.FromContext(harness.App.Ctx).ProxyFor(new Uri(FetchTarget))?.Port);
    }

    [Fact]
    public async Task Probe_ReportsReachableProxy()
    {
        await using var proxy = new TestForwardProxy();

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"http://127.0.0.1:{proxy.Port}", ProbeUrl = FetchTarget },
            TestContext.Current.CancellationToken);

        Assert.True(result.Reachable, result.Message);
        Assert.StartsWith("本机出口代理可用", result.Message);
    }

    [Fact]
    public async Task Probe_ReportsUnreachableProxyLayer_WithoutPassword()
    {
        var port = FreePort();

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"socks5://alice:{EscapedPassword}@127.0.0.1:{port}", ProbeUrl = FetchTarget },
            TestContext.Current.CancellationToken);

        Assert.False(result.Reachable);
        Assert.StartsWith("本机出口代理不通: 无法连接代理", result.Message);
        Assert.Contains($"socks5://***@127.0.0.1:{port}", result.Message);
        Assert.DoesNotContain(EscapedPassword, result.Message);
        Assert.DoesNotContain(Password, result.Message);
    }

    [Fact]
    public async Task Probe_ReportsProxyAuthenticationFailure()
    {
        await using var proxy = new TestForwardProxy(requiredCredential: $"alice:{Password}");

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"http://alice:wrong@127.0.0.1:{proxy.Port}", ProbeUrl = "https://example.test/" },
            TestContext.Current.CancellationToken);

        Assert.False(result.Reachable);
        Assert.Equal($"本机出口代理不通: 代理 http://***@127.0.0.1:{proxy.Port} 要求认证或用户名密码错误(HTTP 407)", result.Message);
    }

    [Fact]
    public async Task Probe_RejectsUnsupportedScheme_WithRedactedReason()
    {
        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"ftp://alice:{EscapedPassword}@127.0.0.1:21" },
            TestContext.Current.CancellationToken);

        Assert.False(result.Reachable);
        Assert.Contains("配置无效", result.Message);
        Assert.Contains("ftp://***@127.0.0.1:21", result.Message);
        Assert.DoesNotContain(EscapedPassword, result.Message);
    }

    [Fact]
    public async Task ProxyCommand_TestsAndReloadsSettings()
    {
        await using var proxy = new TestForwardProxy();
        using var harness = await TestHarness.ComposeAsync(null);
        var settings = HarnessSettings.Load(harness.App.Home);
        settings.Network = new NetworkSettings
        {
            OutboundProxy = new OutboundProxySettings { Url = $"http://127.0.0.1:{proxy.Port}", ProbeUrl = FetchTarget },
        };
        settings.Save(harness.App.Home);

        var test = await harness.CommandAsync("/proxy test");
        var reload = await harness.CommandAsync("/proxy reload");

        Assert.IsType<CommandResult.Success>(test);
        Assert.Contains("本机出口代理可用", ((CommandResult.Success)test).Text);
        Assert.IsType<CommandResult.Success>(reload);
        Assert.Equal(new Uri($"http://127.0.0.1:{proxy.Port}"), OutboundProxy.FromContext(harness.App.Ctx).Address);
    }

    [Fact]
    public async Task SettingsFacade_WritesNetworkSection_KeepsComments_AndAppliesLive()
    {
        using var harness = await TestHarness.ComposeAsync(null);
        var facade = new SettingsFacade(harness.App.Ctx, harness.App.Home);
        var path = facade.SettingsPath;

        facade.UpdateOutboundProxy(new OutboundProxySettings { Url = "http://bob:pw@127.0.0.1:7890", Bypass = ["*.corp.example", "*"] });
        var saved = HarnessSettings.Load(harness.App.Home).Network?.OutboundProxy;
        var written = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => facade.UpdateOutboundProxy(new OutboundProxySettings { Url = "gopher://127.0.0.1:70" }));
        var afterInvalid = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        facade.UpdateOutboundProxy(new OutboundProxySettings());
        var cleared = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains(TestHarness.KeptComment, written);
        Assert.Equal("http://bob:pw@127.0.0.1:7890", saved?.Url);
        Assert.Equal(["*.corp.example", "*"], saved?.Bypass);
        Assert.Equal(written, afterInvalid);
        Assert.DoesNotContain("network:", cleared);
        Assert.Contains(TestHarness.KeptComment, cleared);
        Assert.Null(OutboundProxy.FromContext(harness.App.Ctx).Address);
    }

    [Theory]
    [InlineData("http://user:pass@host:8080", "http://***@host:8080")]
    [InlineData("user:pass@host:8080", "***@host:8080")]
    [InlineData("socks5://host:1080/path@x", "socks5://host:1080/path@x")]
    public void Redact_HidesUserInfoOnly(string url, string expected)
        => Assert.Equal(expected, OutboundProxy.Redact(url));

    [Theory]
    [InlineData("http://localhost/", true)]
    [InlineData("http://127.0.0.1/", true)]
    [InlineData("http://intranet/", true)]
    [InlineData("http://192.168.1.10/", true)]
    [InlineData("http://[fe80::1]/", true)]
    [InlineData("http://api.corp.example/", true)]
    [InlineData("http://corp.example/", true)]
    [InlineData("http://example.com/", false)]
    [InlineData("http://8.8.8.8/", false)]
    public void ConfiguredProxy_BypassesLocalAndListedHosts(string url, bool direct)
    {
        var proxy = new OutboundProxy();
        proxy.Configure("http://127.0.0.1:7890", ["*.corp.example"]);

        Assert.Equal(direct, proxy.ProxyFor(new Uri(url)) is null);
    }

    private static string Text(ToolExecutionResult result) => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /** 组合真实 harness(默认插件全开), settings.yaml 里只放 network.outbound_proxy 与一行需保留的注释。 */
    private sealed class TestHarness(HarnessApp app, string directory) : IDisposable
    {
        public const string KeptComment = "# keep this comment";

        public HarnessApp App { get; } = app;

        public static async Task<TestHarness> ComposeAsync(string? outboundProxyYaml)
        {
            var directory = TempTree.CreateDirectory("outbound-proxy");
            var home = new HarnessHome(Path.Combine(directory, "home"));
            Directory.CreateDirectory(home.Root);
            var network = outboundProxyYaml is null ? "" : $"network:\n  outbound_proxy:\n    {outboundProxyYaml}\n";
            await File.WriteAllTextAsync(
                Path.Combine(home.Root, "settings.yaml"),
                $"{KeptComment}\nglobal_default_model: null\nproviders: {{}}\nlogging:\n  file: false\n{network}");
            return new TestHarness(await ConfigBoot.Compose(new HarnessOptions(home, Cwd: directory)), directory);
        }

        public async Task<ToolExecutionResult> FetchAsync(string url)
        {
            var tools = App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            return await tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = "web_fetch",
                Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["url"] = url }),
                Signal = TestContext.Current.CancellationToken,
            });
        }

        public async Task<CommandResult> CommandAsync(string line)
        {
            var agents = App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var handle = await agents.Create(new CreateAgentOptions(Cwd: directory));
            var commands = App.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            var execution = await commands.Execute(handle.Agent, line, TestContext.Current.CancellationToken);
            return execution!.Result;
        }

        public void Dispose()
        {
            App.Dispose();
            TempTree.Delete(directory);
        }
    }
}

/** 设置页「网络」分页: 草稿自检走同一自检服务, 保存写回 settings.yaml 并立即作用于本进程。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class OutboundProxyGuiTests
{
    [Fact]
    public async Task NetworkSection_ChecksDraftAndSavesLive() => await HeadlessGui.RunAsync(async () =>
    {
        await using var proxy = new TestForwardProxy();
        using var environment = GuiTestEnvironment.CreateBlocking();
        var window = new MainWindow(environment.App, environment.Agent);
        var preferences = window.ViewModel!.Preferences;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            window.ViewModel.ShowSettingsCommand.Execute(null);
            var network = preferences.Sections.First(section => section.Section == SettingsSection.Network);
            network.SelectCommand!.Execute(network);
            preferences.OutboundProxyUrl = $"http://127.0.0.1:{proxy.Port}";
            preferences.OutboundProxyProbeUrl = "http://example.test/";
            await preferences.CheckProxyCommand.ExecuteAsync(null);
            preferences.SaveNetworkCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(preferences.IsNetworkSection);
            Assert.StartsWith("本机出口代理可用", preferences.ProxyCheckResult);
            Assert.Equal(new Uri($"http://127.0.0.1:{proxy.Port}"), OutboundProxy.FromContext(environment.App.Ctx).Address);
            Assert.Equal($"http://127.0.0.1:{proxy.Port}", HarnessSettings.Load(environment.App.Home).Network?.OutboundProxy?.Url);
            var page = window.GetVisualDescendants().OfType<SettingsPageView>().Single();
            Assert.Contains(page.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(), block => block.Text == "本机出口代理");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });
}

/** 测试专用代理: HTTP 模式(绝对 URI 的 GET 与 CONNECT, 可要求 Basic 认证)或 SOCKS5 模式(用户名密码认证), 记录请求后回固定响应。 */
internal sealed class TestForwardProxy : IAsyncDisposable
{
    private const byte SocksVersion = 5;
    private const byte UserPasswordMethod = 2;
    private const byte NoAuthMethod = 0;
    private const byte AddressIpv4 = 1;
    private const byte AddressDomain = 3;
    private const int Ipv4Length = 4;
    private const int Ipv6Length = 16;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _socks;
    private readonly string? _requiredCredential;
    private readonly Task _accepting;
    private int _connections;

    public const string ProxiedBody = "via-test-proxy";

    public TestForwardProxy(bool socks = false, string? requiredCredential = null)
    {
        _socks = socks;
        _requiredCredential = requiredCredential;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref _connections);

    public ConcurrentQueue<string> Requests { get; } = new();

    public ConcurrentQueue<string> Credentials { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref _connections);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            try
            {
                await (_socks ? ServeSocksAsync(stream) : ServeHttpAsync(stream));
            }
            catch (Exception error) when (error is IOException or SocketException or EndOfStreamException or OperationCanceledException)
            {
            }
        }
    }

    private async Task ServeHttpAsync(NetworkStream stream)
    {
        while (await ReadHeadAsync(stream) is { } head)
        {
            var lines = head.Split("\r\n");
            if (_requiredCredential is not null && BasicCredential(lines) != _requiredCredential)
            {
                await WriteAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\n\r\n");
                continue;
            }
            if (_requiredCredential is not null)
                Credentials.Enqueue(_requiredCredential);
            Requests.Enqueue(lines[0]);
            await (lines[0].StartsWith("CONNECT ", StringComparison.Ordinal)
                ? WriteAsync(stream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                : WriteBodyAsync(stream));
            return;
        }
    }

    private async Task ServeSocksAsync(NetworkStream stream)
    {
        var greeting = await ReadExactAsync(stream, 2);
        var methods = await ReadExactAsync(stream, greeting[1]);
        var authenticate = methods.Contains(UserPasswordMethod);
        await stream.WriteAsync(new[] { SocksVersion, authenticate ? UserPasswordMethod : NoAuthMethod });
        if (authenticate)
            await ReadSocksCredentialAsync(stream);
        var request = await ReadExactAsync(stream, 4);
        var host = request[3] switch
        {
            AddressIpv4 => new IPAddress(await ReadExactAsync(stream, Ipv4Length)).ToString(),
            AddressDomain => Encoding.ASCII.GetString(await ReadExactAsync(stream, (await ReadExactAsync(stream, 1))[0])),
            _ => new IPAddress(await ReadExactAsync(stream, Ipv6Length)).ToString(),
        };
        var port = await ReadExactAsync(stream, 2);
        Requests.Enqueue($"SOCKS {host}:{(port[0] << 8) | port[1]}");
        await stream.WriteAsync(new byte[] { SocksVersion, 0, 0, AddressIpv4, 0, 0, 0, 0, 0, 0 });
        if (await ReadHeadAsync(stream) is { } head)
            Requests.Enqueue(head.Split("\r\n")[0]);
        await WriteBodyAsync(stream);
    }

    private async Task ReadSocksCredentialAsync(NetworkStream stream)
    {
        var header = await ReadExactAsync(stream, 2);
        var user = Encoding.UTF8.GetString(await ReadExactAsync(stream, header[1]));
        var password = Encoding.UTF8.GetString(await ReadExactAsync(stream, (await ReadExactAsync(stream, 1))[0]));
        Credentials.Enqueue($"{user}:{password}");
        await stream.WriteAsync(new byte[] { 1, 0 });
    }

    private static string? BasicCredential(string[] lines)
        => lines.FirstOrDefault(line => line.StartsWith("Proxy-Authorization: Basic ", StringComparison.OrdinalIgnoreCase)) is { } header
            ? Encoding.UTF8.GetString(Convert.FromBase64String(header["Proxy-Authorization: Basic ".Length..].Trim()))
            : null;

    private static Task WriteBodyAsync(NetworkStream stream)
        => WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {ProxiedBody.Length}\r\nConnection: close\r\n\r\n{ProxiedBody}");

    private static async Task WriteAsync(NetworkStream stream, string text)
        => await stream.WriteAsync(Encoding.ASCII.GetBytes(text));

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0)
                return null;
            bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString([.. bytes]);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }
}
