using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Runtime;

namespace Dsh.WebBridge;

/** Keeps the foreground bridge alive only while its plugin is active. */
public sealed class WebBridgeRuntime(OutboundProxy? proxy = null) : IDisposable
{
    public const string ServiceName = "webBridge";

    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private Task<int>? _run;
    private bool _disposed;

    public Task<int> RunAsync(PluginEntrypointOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is not null)
                throw new InvalidOperationException("the web bridge is already running");
            return _run = RunCoreAsync(options, output, error, cancellationToken);
        }
    }

    private async Task<int> RunCoreAsync(PluginEntrypointOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        Func<MachineSpec, CancellationToken, Task>? startHost = options.StartLocalHost is { } start
            ? (machine, token) => start(machine.HarnessHome ?? options.Home.Root, token)
            : null;
        try
        {
            return await WebBridgeCommand.RunAsync(options.Home.Root, options.Arguments ?? [], output, error,
                () => options.StartLocalHost?.Invoke(options.Home.Root, stop.Token) ?? Task.CompletedTask,
                stop.Token, startHost, proxy).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        Task<int>? run;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            run = _run;
        }
        try
        {
            _stop.Cancel();
            run?.GetAwaiter().GetResult();
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
