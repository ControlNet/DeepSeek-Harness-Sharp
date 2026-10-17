using Dsh.Boot;
using Dsh.Fleet;

namespace Dsh.WebBridge;

/** HTTP composition lives here; the activated neutral plugin remains the owner of the actual Fleet and listener lifetime. */
public sealed class WebBridgeApplication : IWebBridgeApplication
{
    public Task<int> RunAsync(WebBridgeRuntime runtime, PluginEntrypointOptions options, CancellationToken cancellationToken)
    {
        Func<MachineSpec, CancellationToken, Task>? startHost = options.StartLocalHost is { } start
            ? (machine, token) => start(machine.HarnessHome ?? options.Home.Root, token)
            : null;
        return WebBridgeCommand.RunAsync(options.Home.Root, options.Arguments ?? [], Console.Out, Console.Error,
            () => options.StartLocalHost?.Invoke(options.Home.Root, cancellationToken) ?? Task.CompletedTask,
            cancellationToken, startHost, runtime.Proxy, runtime);
    }
}
