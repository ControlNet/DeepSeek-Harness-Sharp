using Dsh.Boot;
using Dsh.Runtime;

namespace Dsh.Interaction;

/** /proxy: 查看、写回并热更新本机出口; test 与 GUI 共用自检; reload 重新读取 settings.yaml。 */
public static class ProxyCommand
{
    private const string Usage = "usage: /proxy [set <proxy-url> | clear | test [probe-url] | reload]";

    public static IDisposable Register(Context ctx, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false)!;
        return commands.Register(new CommandDefinition
        {
            Name = "proxy",
            Description = "Show/save the local outbound proxy; test checks connectivity, reload applies settings.yaml",
            Input = new CommandInputDescriptor("set <proxy-url> | clear | test [probe-url] | reload"),
            Handler = invocation => RunAsync(OutboundProxy.FromContext(ctx), home, invocation),
        });
    }

    private static async Task<CommandResult> RunAsync(OutboundProxy proxy, HarnessHome home, CommandInvocation invocation)
    {
        var settings = HarnessSettings.Load(home).Network?.OutboundProxy ?? new OutboundProxySettings();
        var configured = string.IsNullOrWhiteSpace(settings.Url) ? "留空(跟随系统代理)" : OutboundProxy.Redact(settings.Url);
        switch (invocation.RawInput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            case []:
                return new CommandResult.Success($"本机出口代理: 当前生效 {proxy.Describe()}; settings.yaml network.outbound_proxy.url: {configured}");
            case ["reload"]:
                return Reload(proxy, settings);
            case ["set", var url]:
                return Save(proxy, home, url);
            case ["clear"]:
                return Save(proxy, home, null);
            case ["test", .. var rest] when rest.Length <= 1:
                settings.ProbeUrl = rest is [var probeUrl] ? probeUrl : settings.ProbeUrl;
                var result = await OutboundProxyProbe.RunAsync(settings, invocation.Signal);
                return result.Reachable ? new CommandResult.Success(result.Message) : new CommandResult.Error(result.Message);
            default:
                return new CommandResult.Error(Usage);
        }
    }

    private static CommandResult Save(OutboundProxy proxy, HarnessHome home, string? url)
    {
        var settings = HarnessSettings.Load(home);
        settings.Network ??= new NetworkSettings();
        settings.Network.OutboundProxy ??= new OutboundProxySettings();
        var outbound = settings.Network.OutboundProxy;
        try
        {
            proxy.Configure(url, outbound.Bypass);
        }
        catch (ArgumentException error)
        {
            return new CommandResult.Error(error.Message);
        }
        outbound.Url = url;
        settings.Save(home);
        return new CommandResult.Success($"本机出口代理已保存并生效: {proxy.Describe()}");
    }

    private static CommandResult Reload(OutboundProxy proxy, OutboundProxySettings settings)
    {
        try
        {
            proxy.Configure(settings.Url, settings.Bypass);
            return new CommandResult.Success($"本机出口代理已应用: {proxy.Describe()}");
        }
        catch (ArgumentException error)
        {
            return new CommandResult.Error(error.Message);
        }
    }
}
