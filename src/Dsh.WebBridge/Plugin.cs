using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-web-bridge")]

namespace Dsh.WebBridge;

/** Browser frontend entry; the activation owns the server and all of its client links. */
[DshEntrypoint("web")]
public sealed class Plugin : IDshPlugin, IDshEntrypoint
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        var runtime = new WebBridgeRuntime(OutboundProxy.FromContext(ctx));
        ctx.Provide(WebBridgeRuntime.ServiceName, runtime);
        return runtime;
    }

    public Task<int> RunAsync(HarnessApp app, PluginEntrypointOptions options, CancellationToken cancellationToken)
        => (app.Ctx.Get<WebBridgeRuntime>(WebBridgeRuntime.ServiceName)
            ?? throw new InvalidOperationException("the web bridge plugin is not active"))
            .RunAsync(options, Console.Out, Console.Error, cancellationToken);
}
