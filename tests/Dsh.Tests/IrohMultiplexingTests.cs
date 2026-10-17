using Dsh.Fleet;

namespace Dsh.Tests;

public sealed class IrohMultiplexingTests
{
    [Fact(Timeout = 60000)]
    public async Task ExistingTcpMaster_CannotExecuteCommandsForAnIrohEndpoint()
    {
        _ = FleetSshEndToEndTests.RequireEnvironment();
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestSshServer.StartAsync(ct);
        var shared = TempTree.CreateSocketDirectory("im");
        var socket = Path.Combine(shared, "master.sock");
        var source = server.Machine("master", server.Home);
        var master = source with
        {
            SshOptions = [.. source.SshOptions!.Where(option => !option.StartsWith("Control", StringComparison.OrdinalIgnoreCase)),
                "ControlMaster=auto", "ControlPersist=60", $"ControlPath={socket}"],
        };
        try
        {
            Assert.Equal("tcp-master-ok", await SshHostLauncher.RunRemoteAsync(master, "printf '%s' tcp-master-ok", ct));
            await TestSshServer.RunAsync("ssh", ["-S", socket, "-O", "check", source.Target], ct);
            var endpoint = master with { Kind = MachineKinds.Iroh, Host = "", EndpointId = new string('0', IrohSshTransport.EndpointIdLength), RelayUrls = ["http://127.0.0.1:9"] };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var observed = await SshHostLauncher.RunRemoteCaptureAsync(endpoint, "printf '%s' multiplexed-tcp-bypass; touch route-bypassed", attempt.Token);
                Assert.NotEqual(0, observed.ExitCode);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
            }
            Assert.False(File.Exists(Path.Combine(server.Home, "route-bypassed")));
            Assert.Equal("tcp-master-still-ok", await SshHostLauncher.RunRemoteAsync(master, "printf '%s' tcp-master-still-ok", ct));
        }
        finally
        {
            await TestSshServer.RunAsync("ssh", ["-S", socket, "-O", "exit", source.Target], CancellationToken.None);
            TempTree.Delete(shared);
        }
    }
}
