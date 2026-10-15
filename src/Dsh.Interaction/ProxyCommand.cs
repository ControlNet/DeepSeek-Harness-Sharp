using Dsh.Boot;
using Dsh.Runtime;

namespace Dsh.Interaction;

/** /proxy: 查看本机出口代理; test 按 settings.yaml 做连通性自检(与 GUI 设置页同一自检); reload 把 settings.yaml 应用到当前进程。 */
public static class ProxyCommand
{
    private const string Usage = "usage: /proxy [test [probe-url] | reload]";

    public static IDisposable Register(Context ctx, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false)!;
        return commands.Register(new CommandDefinition
        {
            Name = "proxy",
            Description = "Show the local outbound proxy; /proxy test checks connectivity, /proxy reload applies settings.yaml",
            Input = new CommandInputDescriptor("test [probe-url] | reload"),
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
            case ["test", .. var rest] when rest.Length <= 1:
                settings.ProbeUrl = rest is [var probeUrl] ? probeUrl : settings.ProbeUrl;
                var result = await OutboundProxyProbe.RunAsync(settings, invocation.Signal);
                return result.Reachable ? new CommandResult.Success(result.Message) : new CommandResult.Error(result.Message);
            default:
                return new CommandResult.Error(Usage);
        }
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
