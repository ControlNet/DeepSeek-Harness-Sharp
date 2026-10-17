using Dsh.Boot;
using Dsh.Fleet;

namespace Dsh.Tests;

public sealed class IrohReceiverEndToEndTests
{
    [Theory(Timeout = 180000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetCli_Readiness_And_Restart_Work_Without_A_Direct_Host_Address(bool directOnly)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestSshServer.StartAsync(ct);
        var root = TempTree.CreateSocketDirectory("ie");
        // A dead custom relay must still permit discovery and transport through the LAN.
        var direct = server.Machine("receiver", root);
        string[] relays = directOnly ? ["http://127.0.0.1:9"] : [];
        var readyPath = IrohReceiver.ReadinessPath(IrohReceiver.ReceiverRoot(new HarnessHome(root)));
        var command = $"{RemoteShell.Prelude(direct)}{RemoteShell.Quote(executable)} remote";
        var enable = $"{command} enable --ssh-port {direct.Port}{(directOnly ? " --relay-url http://127.0.0.1:9" : "")}";
        var activation = Guid.NewGuid().ToString("N");
        try
        {
            var first = IrohRemoteInstaller.ParseStatus(await new SshTransport(direct).RunAsync($"{enable} --activation {activation}", ct));
            Assert.True(first.Running);
            var tunneled = direct with { Kind = MachineKinds.Iroh, Host = "192.0.2.1", EndpointId = first.EndpointId, RelayUrls = relays };
            var observed = IrohRemoteInstaller.ParseStatus(await new SshTransport(tunneled).RunAsync($"{command} info", ct));
            Assert.True(observed.Running);
            var otherActivation = Guid.NewGuid().ToString("N");
            Assert.Equal(first.EndpointId, IrohRemoteInstaller.ParseStatus(await new SshTransport(direct).RunAsync($"{enable} --activation {otherActivation}", ct)).EndpointId);
            await new SshTransport(direct).RunAsync($"{command} disable --activation {otherActivation}", ct);
            Assert.Equal("receiver-ready", await new SshTransport(tunneled).RunAsync("printf '%s' receiver-ready", ct));
            await new SshTransport(direct).RunAsync($"{command} disable --activation {activation}", ct);
            var stopped = IrohRemoteInstaller.ParseStatus(await new SshTransport(direct).RunAsync($"{command} info", ct));
            Assert.False(stopped.Running);
            Assert.Equal(first.EndpointId, stopped.EndpointId);
            var canceled = Guid.NewGuid().ToString("N");
            await new SshTransport(direct).RunAsync($"{command} disable --activation {canceled}", ct);
            var delayed = await new SshTransport(direct).CaptureAsync($"{enable} --activation {canceled}", ct);
            Assert.NotEqual(0, delayed.ExitCode);
            Assert.False(IrohRemoteInstaller.ParseStatus(await new SshTransport(direct).RunAsync($"{command} info", ct)).Running);
            // Startup recovers from a stale acknowledgement left by an interrupted generation.
            await File.WriteAllTextAsync(readyPath, "stale discovery acknowledgement\n", ct);
            var restarted = IrohRemoteInstaller.ParseStatus(await new SshTransport(direct).RunAsync(enable, ct));
            await new SshTransport(direct).RunAsync($"{command} disable --activation {activation}", ct);
            Assert.True(restarted.Running);
            Assert.Equal(first.EndpointId, restarted.EndpointId);
            Assert.Equal("receiver-restarted", await new SshTransport(tunneled).RunAsync("printf '%s' receiver-restarted", ct));
        }
        catch
        {
            var log = Path.Combine(IrohReceiver.ReceiverRoot(new HarnessHome(root)), "receiver.log");
            if (File.Exists(log))
                TestContext.Current.TestOutputHelper?.WriteLine(await File.ReadAllTextAsync(log, CancellationToken.None));
            throw;
        }
        finally
        {
            await IrohReceiver.DisableAsync(new HarnessHome(root), null, CancellationToken.None);
            TempTree.Delete(root);
        }
    }
}
