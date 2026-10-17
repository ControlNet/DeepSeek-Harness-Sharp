using Dsh.Boot;
using Dsh.Runtime;

namespace Dsh.Fleet;

/** Plugin-lifetime owner of frontend clients, never host sessions or a second connection/state cache. */
public sealed class FleetService(HarnessHome home, Context? context = null) : IDisposable
{
    public const string ServiceName = "fleet";

    private readonly object _sync = new();
    private readonly List<FleetClient> _clients = [];
    private bool _disposed;

    public HarnessHome Home { get; } = home;
    public MachineStore Store { get; } = new(home);

    public FleetClient CreateClient(FleetOptions options)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var config = Store.Load();
            var client = new FleetClient([MachineSpec.Local(Home.Root), .. config.Machines], options with
            {
                Deploy = config.Deploy,
                LocalProxy = context is null ? options.LocalProxy : OutboundProxy.FromContext(context),
            });
            client.Disposed += RemoveClient;
            _clients.Add(client);
            return client;
        }
    }

    private void RemoveClient(FleetClient client)
    {
        client.Disposed -= RemoveClient;
        lock (_sync)
            _clients.Remove(client);
    }

    public void Dispose()
    {
        FleetClient[] clients;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            clients = [.. _clients];
            _clients.Clear();
        }
        List<Exception>? errors = null;
        foreach (var client in clients)
        {
            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
        if (errors is not null)
            throw new AggregateException(errors);
    }
}
