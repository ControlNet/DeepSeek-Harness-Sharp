using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-fleet")]

namespace Dsh.Fleet;

/** Activates the shared machine service; unloading closes frontend links, not host sessions. */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        var root = ctx.GetProp("dshHomePath") as string
            ?? throw new InvalidOperationException("the fleet plugin requires a harness home");
        var service = new FleetService(new HarnessHome(root), ctx);
        ctx.Provide(FleetService.ServiceName, service);
        return service;
    }
}
