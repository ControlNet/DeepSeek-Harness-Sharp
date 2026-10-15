using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Transport;
using Dsh.WebBridge;

namespace Dsh.Tests;

/**
 * Hub mode over real ssh: the bridge deploys dsharp to a user-mode sshd "remote" through its status connection, then gives every browser WebSocket its own `ssh -T ... host --serve --stdio` byte tunnel.
 * Same sshd recipe as FleetSshEndToEndTests: test keys and known_hosts in the test directory, so the real ~/.ssh and ~/.dsharp stay untouched.
 */
[Collection(SerialProcessCollection.Name)]
public sealed class WebBridgeSshTunnelTests
{
    private const string RemoteMachine = "e2e";

    private static readonly TimeSpan DeployTimeout = TimeSpan.FromMinutes(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 360000)]
    public async Task EveryWebSocket_GetsItsOwnSshTunnel_ToTheDeployedRemoteHost()
    {
        var ct = TestContext.Current.CancellationToken;
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var server = await TestSshServer.StartAsync(ct);
        var bridgeHome = RealHostProcesses.CreateHome("wt");
        var remoteHome = TempTree.CreateSocketDirectory("wr");
        try
        {
            var machine = server.Machine(RemoteMachine, remoteHome);
            var options = FleetConnectionTests.TestOptions with
            {
                Deploy = new DeploySettings { Via = DownloadVia.Client },
                ArchiveFactory = (_, rid, token) => FleetSshEndToEndTests.BuildArchiveAsync(executable, rid, server.Root, token),
            };
            await using var bridge = WebBridgeServer.Start(new WebBridgeOptions(bridgeHome, 0, [MachineSpec.Local(bridgeHome), machine], options));
            var access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(bridgeHome))!);
            await access.WaitForMachinesAsync(states => states[RemoteMachine] == "Connected", DeployTimeout, ct);
            Assert.Equal(1, SshChildren(machine.Port));

            var cookies = await access.LoginAsync(ct);
            using var first = await access.ConnectWebSocketAsync(cookies, access.Origin, ct, RemoteMachine);
            using var second = await access.ConnectWebSocketAsync(cookies, access.Origin, ct, RemoteMachine);
            foreach (var socket in new[] { first, second })
            {
                await WebBridgeHubTests.HelloAsync(socket);
                Assert.Equal(remoteHome, (await WebSocketRpc.RequestAsync(socket, 2, HostProtocol.MethodInfo, null)).GetProperty("HomeRoot").GetString());
            }

            Assert.Equal(3, SshChildren(machine.Port));
            var started = await WebSocketRpc.RequestAsync(first, 3, HostProtocol.MethodPtyStart, $$"""{"FileName":"/bin/sh","Arguments":["-c","echo tunnel-$((6*7)); sleep 30"],"Cwd":{{JsonSerializer.Serialize(remoteHome)}}}""");
            var ptyId = started.GetProperty("PtyId").GetString()!;
            await WebSocketRpc.SendAsync(second, 4, HostProtocol.MethodPtyAttach, $$"""{"PtyId":"{{ptyId}}"}""");
            await WebBridgeHubTests.ReadTerminalUntilAsync(second, ptyId, "tunnel-42");

            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
            await WebSocketRpc.WaitForAsync(() => SshChildren(machine.Port) == 2);
        }
        finally
        {
            RealHostProcesses.KillAll(remoteHome);
            TempTree.Delete(remoteHome);
            TempTree.Delete(bridgeHome);
        }

        guard.AssertUnchanged();
    }

    /** Live ssh processes this test process started for the sshd on port: the bridge's status connection plus one tunnel per open WebSocket. */
    private static int SshChildren(int port)
    {
        var portArgument = Encoding.UTF8.GetBytes($"\0-p\0{port}\0");
        var count = 0;
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out _) || !IsChildOfThisProcess(directory))
                continue;
            var command = ReadOrEmpty(Path.Combine(directory, "cmdline"));
            var program = Encoding.UTF8.GetString(command.AsSpan(0, Math.Max(0, command.AsSpan().IndexOf((byte)0))));
            if (Path.GetFileName(program) == "ssh" && command.AsSpan().IndexOf(portArgument) >= 0)
                count++;
        }

        return count;
    }

    /** /proc/<pid>/stat is "pid (comm) state ppid ..."; comm may contain spaces, so the fields are read after the last ')'. */
    private static bool IsChildOfThisProcess(string processDirectory)
    {
        var stat = Encoding.UTF8.GetString(ReadOrEmpty(Path.Combine(processDirectory, "stat")));
        var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && fields[0] != "Z" && int.TryParse(fields[1], out var parent) && parent == Environment.ProcessId;
    }

    private static byte[] ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
