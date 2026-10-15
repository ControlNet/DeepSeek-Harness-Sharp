using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Notifications;

namespace Dsh.Gui.Services;

/**
 * The GUI's one connection to every machine: owns the FleetClient and re-raises its background events on the UI thread.
 * Changed is coalesced (one UI callback per burst of state, session or pending updates); notification events are delivered one by one in arrival order.
 * Reload replaces the client after the machine list was saved, so the views always show the stored machines.
 */
public sealed class FleetWorkspace : IAsyncDisposable
{
    private readonly Func<FleetClient> _factory;
    private readonly DispatcherBridge<bool> _changes;
    private bool _disposed;

    public FleetWorkspace(Func<FleetClient> factory)
    {
        _factory = factory;
        _changes = new DispatcherBridge<bool>(_ => Changed?.Invoke());
        Client = Attach(factory());
    }

    public FleetClient Client { get; private set; }

    /** Raised on the UI thread after any machine's state, sessions or pending requests changed, and after a reload. */
    public event Action? Changed;

    public event Action<MachineConnection, NotificationRecord>? NotificationAdded;

    public event Action<MachineConnection, IReadOnlyList<string>>? NotificationsRead;

    public event Action<MachineConnection>? NotificationsReloaded;

    /**
     * This computer's harness home first (it is also the home the GUI itself runs on), then every machine of its store.
     * startLocalHost launches `host serve` for a local machine whose endpoint is not reachable; without it local machines are only connected when their host already runs.
     */
    public static FleetWorkspace ForHome(HarnessHome home, Func<string, CancellationToken, Task>? startLocalHost)
        => new(() =>
        {
            var config = new MachineStore(home).Load();
            var options = new FleetOptions
            {
                ClientName = GuiSettings.RemoteClientName,
                Deploy = config.Deploy,
                StartLocalHost = startLocalHost is null ? null : (machine, token) => startLocalHost(machine.HarnessHome ?? home.Root, token),
            };
            return new FleetClient([MachineSpec.Local(home.Root), .. config.Machines], options);
        });

    public MachineConnection? Find(string machine) => Client.Find(machine);

    public void Start() => Client.Start();

    /** Rebuilds the client from the factory (the machine list may have changed) and starts it. */
    public async Task ReloadAsync()
    {
        var previous = Client;
        Detach(previous);
        Client = Attach(_factory());
        Client.Start();
        _changes.Enqueue(true);
        await previous.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach(Client);
        await Client.DisposeAsync();
    }

    private FleetClient Attach(FleetClient client)
    {
        client.Changed += OnFleetChanged;
        foreach (var machine in client.Machines)
        {
            machine.NotificationAdded += OnNotificationAdded;
            machine.NotificationsRead += OnNotificationsRead;
            machine.NotificationsReloaded += OnNotificationsReloaded;
        }
        return client;
    }

    private void Detach(FleetClient client)
    {
        client.Changed -= OnFleetChanged;
        foreach (var machine in client.Machines)
        {
            machine.NotificationAdded -= OnNotificationAdded;
            machine.NotificationsRead -= OnNotificationsRead;
            machine.NotificationsReloaded -= OnNotificationsReloaded;
        }
    }

    private void OnFleetChanged() => _changes.Enqueue(true);

    private void OnNotificationAdded(MachineConnection machine, NotificationRecord record)
        => Dispatcher.UIThread.Post(() => NotificationAdded?.Invoke(machine, record));

    private void OnNotificationsRead(MachineConnection machine, IReadOnlyList<string> ids)
        => Dispatcher.UIThread.Post(() => NotificationsRead?.Invoke(machine, ids));

    private void OnNotificationsReloaded(MachineConnection machine)
        => Dispatcher.UIThread.Post(() => NotificationsReloaded?.Invoke(machine));
}
