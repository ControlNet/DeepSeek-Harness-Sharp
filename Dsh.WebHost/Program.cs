using Dsh.Boot;
using Dsh.Host;
using Dsh.WebBridge;

// A detached local Host uses the current executable. Reuse the normal host command rather than
// guessing another executable's name or introducing a second daemon launcher.
if (args.Contains("host"))
    return await HarnessEntrypoint.RunAsync(args);
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("usage: dsharp-web [--port N] [--print-token]");
    return 0;
}
var home = HarnessStorage.ResolveDefaultHome();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
using var termination = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
using var app = await ConfigBoot.Compose(new HarnessOptions(home, Directory.GetCurrentDirectory(), IsTui: false));
app.Ctx.Provide(WebBridgeRuntime.ApplicationServiceName, new WebBridgeApplication());
Task StartHost(string root, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    HostAutostart.StartDetached(null, root);
    return Task.CompletedTask;
}
try
{
    return await app.RunEntrypointAsync("web", new PluginEntrypointOptions(home, Directory.GetCurrentDirectory(), StartLocalHost: StartHost, Arguments: args), stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
