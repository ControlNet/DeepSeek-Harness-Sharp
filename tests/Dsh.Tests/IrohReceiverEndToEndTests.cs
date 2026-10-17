using Dsh.Boot;
using Dsh.Fleet;

namespace Dsh.Tests;

public sealed class IrohReceiverEndToEndTests
{
    [Fact(Timeout = 180000)]
    public async Task TargetCli_Readiness_And_Restart_Work_Without_A_Direct_Host_Address()
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestSshServer.StartAsync(ct);
        var root = TempTree.CreateSocketDirectory("ie");
        var direct = server.Machine("receiver", root) with { RelayUrls = ["http://127.0.0.1:9"] };
        var command = $"{RemoteShell.Prelude(direct)}{RemoteShell.Quote(executable)} remote";
        var enable = $"{command} enable --ssh-port {direct.Port} --relay-url http://127.0.0.1:9";
        var activation = Guid.NewGuid().ToString("N");
        try
        {
            var first = IrohRemoteInstaller.ParseStatus(await SshHostLauncher.RunRemoteAsync(direct, $"{enable} --activation {activation}", ct));
            Assert.True(first.Running);
            var tunneled = direct with { Kind = MachineKinds.Iroh, Host = "192.0.2.1", EndpointId = first.EndpointId };
            var observed = IrohRemoteInstaller.ParseStatus(await SshHostLauncher.RunRemoteAsync(tunneled, $"{command} info", ct));
            Assert.True(observed.Running);
            var otherActivation = Guid.NewGuid().ToString("N");
            Assert.Equal(first.EndpointId, IrohRemoteInstaller.ParseStatus(await SshHostLauncher.RunRemoteAsync(direct, $"{enable} --activation {otherActivation}", ct)).EndpointId);
            await SshHostLauncher.RunRemoteAsync(direct, $"{command} disable --activation {otherActivation}", ct);
            Assert.Equal("receiver-ready", await SshHostLauncher.RunRemoteAsync(tunneled, "printf '%s' receiver-ready", ct));
            await SshHostLauncher.RunRemoteAsync(direct, $"{command} disable --activation {activation}", ct);
            var stopped = IrohRemoteInstaller.ParseStatus(await SshHostLauncher.RunRemoteAsync(direct, $"{command} info", ct));
            Assert.False(stopped.Running);
            Assert.Equal(first.EndpointId, stopped.EndpointId);
            var canceled = Guid.NewGuid().ToString("N");
            await SshHostLauncher.RunRemoteAsync(direct, $"{command} disable --activation {canceled}", ct);
            var delayed = await SshHostLauncher.RunRemoteCaptureAsync(direct, $"{enable} --activation {canceled}", ct);
            Assert.NotEqual(0, delayed.ExitCode);
            Assert.False(IrohRemoteInstaller.ParseStatus(await SshHostLauncher.RunRemoteAsync(direct, $"{command} info", ct)).Running);
            var restarted = IrohRemoteInstaller.ParseStatus(await SshHostLauncher.RunRemoteAsync(direct, enable, ct));
            await SshHostLauncher.RunRemoteAsync(direct, $"{command} disable --activation {activation}", ct);
            Assert.True(restarted.Running);
            Assert.Equal(first.EndpointId, restarted.EndpointId);
            Assert.Equal("receiver-restarted", await SshHostLauncher.RunRemoteAsync(tunneled, "printf '%s' receiver-restarted", ct));
        }
        finally
        {
            await IrohReceiver.DisableAsync(new HarnessHome(root), null, CancellationToken.None);
            TempTree.Delete(root);
        }
    }
}
