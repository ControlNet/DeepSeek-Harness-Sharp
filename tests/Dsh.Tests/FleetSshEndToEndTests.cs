using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

/**
 * Dsh.Fleet over real ssh: a user-mode sshd on a loopback port (test-only keys, AuthorizedKeysFile in the test directory, UsePAM no) stands in for a remote machine.
 * The sshd gives sessions a test HOME and starts them there, so deployment lands in the test directory exactly as it would in a real home; nothing under the real ~/.ssh or ~/.dsharp is touched.
 * Covers automatic deployment, the on-demand detached host, `host --serve --stdio`, listing sessions, reconnecting after the ssh process dies, and the CLI on that machine.
 */
public sealed class FleetSshEndToEndTests
{
    private const string Sshd = "/usr/sbin/sshd";

    /** The sshd serves this machine, so deployment picks this machine's runtime identifier. */
    private static readonly string Rid = RuntimeInformation.RuntimeIdentifier;

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMinutes(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 360000)]
    public async Task Deploys_Serves_Lists_AndReconnects_OverRealSsh()
    {
        var executable = RequireEnvironment();
        var guard = RealHomeGuard.Capture();
        await using var server = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var harness = TempTree.CreateSocketDirectory("fe");
        try
        {
            var machine = server.Machine("e2e", harness);
            var options = FleetConnectionTests.TestOptions with
            {
                Deploy = new DeploySettings { Via = DownloadVia.Client },
                ArchiveFactory = (_, rid, token) => BuildArchiveAsync(executable, rid, server.Root, token),
            };
            await using var connection = new MachineConnection(machine, options);
            var states = FleetConnectionTests.RecordStates(connection);
            connection.Start();
            using (var settle = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                settle.CancelAfter(SettleTimeout);
                await connection.WaitSettledAsync(settle.Token);
            }

            Assert.True(connection.State == MachineConnectionState.Connected, connection.Detail);
            Assert.Contains(MachineConnectionState.Deploying, states);
            var deployed = Path.Combine(server.Home, ".dsharp", Rid);
            Assert.Equal(RemoteHostServer.ReleaseVersion, await File.ReadAllTextAsync(Path.Combine(deployed, ".dsharp-version"), Ct));
            Assert.Equal(harness, connection.Info!.HomeRoot);
            Assert.True(File.Exists(Path.Combine(harness, "run", "host.sock")), "the first connection must start the host under the machine's harness home");
            Assert.False(Directory.Exists(Path.Combine(server.Home, ".config", "systemd", "user")), "deployment and on-demand startup must not install a user service");

            var transport = new SshTransport(machine);
            var reused = await new RemoteDsharpInstaller(transport).EnsureAsync(options.Deploy, null, Ct, options.ArchiveFactory);
            Assert.True(reused.AlreadyPresent);
            Assert.Equal(Path.Combine(deployed, "dsharp"), reused.CommandPath);

            var lease = await connection.WaitConnectedAsync(Ct);
            var hostIdentity = LocalEndpointToken.Read(RemoteHostEndpoint.EndpointFile(harness));
            Assert.NotNull(hostIdentity);
            var shell = await lease.Client.Pty.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", [], harness, Title: "e2e-shell"), Ct);
            await FleetConnectionTests.WaitForAsync(() => connection.Sessions.Any(session => session.SessionId == shell));
            await using (var before = RemoteHostPtyTests.TerminalCapture.Attach(lease.Client, shell))
            {
                await before.SendAsync("printf '%s\\n' \"$$\" > before.pid; echo before-$((40+2))\n");
                await before.WaitForAsync("before-42", TimeSpan.FromSeconds(20));
            }
            var shellPid = await File.ReadAllTextAsync(Path.Combine(harness, "before.pid"), Ct);
            var duplicate = await transport.CaptureAsync(
                $"{RemoteShell.Prelude(machine)}{RemoteShell.Quote(reused.CommandPath)} host serve", Ct);
            Assert.True(duplicate.ExitCode == 0, duplicate.FailureDetail);
            Assert.True(hostIdentity == LocalEndpointToken.Read(RemoteHostEndpoint.EndpointFile(harness)),
                "a competing host start must leave the live owner and endpoint untouched");

            var ssh = connection.LinkProcess!;
            ssh.Kill();
            await FleetConnectionTests.WaitForAsync(() => states.Contains(MachineConnectionState.Reconnecting)
                && connection.State == MachineConnectionState.Connected && connection.LinkProcess is { } next && next != ssh);
            await FleetConnectionTests.WaitForAsync(() => connection.Sessions.Any(session => session.SessionId == shell && session.Kind == HostSessionKinds.Terminal));
            Assert.True(hostIdentity == LocalEndpointToken.Read(RemoteHostEndpoint.EndpointFile(harness)),
                "dropping only the SSH bridge must not replace the host process");
            var reconnected = await connection.WaitConnectedAsync(Ct);
            var registered = Assert.Single(await reconnected.Client.ListHostSessionsAsync(Ct), session => session.SessionId == shell);
            Assert.Equal(HostSessionStatuses.Running, registered.Status);
            Assert.Equal("e2e-shell", registered.Title);
            await using (var after = RemoteHostPtyTests.TerminalCapture.Attach(reconnected.Client, shell))
            {
                await after.SendAsync("printf '%s\\n' \"$$\" > after.pid; echo after-$((50+5))\n");
                await after.WaitForAsync("after-55", TimeSpan.FromSeconds(20));
            }
            Assert.Equal(shellPid, await File.ReadAllTextAsync(Path.Combine(harness, "after.pid"), Ct));

            await AssertCliSeesTheMachineAsync(executable, machine, shell);

            await transport.RunAsync($"chmod 755 {RemoteShell.Quote(Path.Combine(harness, "run"))}", Ct);
            try
            {
                var diagnostics = await FleetDoctor.CheckAsync(machine, options, Ct, transport);
                var permissions = Assert.Single(diagnostics, check => check.Name == "permissions");
                Assert.Equal(DoctorOutcome.Failed, permissions.Outcome);
                Assert.Contains("chmod 700", permissions.Fix, StringComparison.Ordinal);
            }
            finally
            {
                await transport.RunAsync($"chmod 700 {RemoteShell.Quote(Path.Combine(harness, "run"))}", Ct);
            }

            var rejected = machine with { KeyPath = Path.Combine(server.Root, "host_key") };
            var authentication = await FleetDoctor.CheckAsync(rejected, options, Ct);
            var failed = Assert.Single(authentication);
            Assert.Equal("ssh", failed.Name);
            Assert.Equal(DoctorOutcome.Failed, failed.Outcome);
            Assert.Contains("Permission denied", failed.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RealHostProcesses.KillAll(harness);
            TempTree.Delete(harness);
        }

        guard.AssertUnchanged();
    }

    [Fact(Timeout = 360000)]
    public async Task Remote_Download_Through_Real_Ssh_Proxy_Deploys_A_Working_Host()
    {
        var executable = RequireEnvironment();
        if (!File.Exists("/usr/bin/curl") && !File.Exists("/usr/bin/wget"))
            Assert.Skip("remote-download coverage needs curl or wget on the loopback SSH server");
        var guard = RealHomeGuard.Capture();
        await using var server = await TestSshServer.StartAsync(Ct);
        var harness = TempTree.CreateSocketDirectory("fd");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        try
        {
            var direct = server.Machine("download-proxy", harness);
            // Another authenticated SSH connection to the real sshd proxies direct-tcpip, not an echo stand-in.
            var proxy = $"ssh -T -p {direct.Port} -i {RemoteShell.Quote(direct.KeyPath!)} "
                + string.Join(' ', direct.SshOptions!.Select(option => $"-o {RemoteShell.Quote(option)}"))
                + $" -o BatchMode=yes -o IdentitiesOnly=yes -W %h:%p {direct.User}@{direct.Host}";
            var machine = direct with { Proxy = proxy };
            var archive = await BuildArchiveAsync(executable, Rid, server.Root, Ct);
            var serving = ServeReleaseAsync(listener, archive, transferCancellation.Token);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var settings = new DeploySettings
            {
                Via = DownloadVia.Server,
                ReleaseBaseUrl = $"http://127.0.0.1:{port}/releases",
            };
            var transport = new SshTransport(machine);
            var deployed = await new RemoteDsharpInstaller(transport).EnsureAsync(settings, null, Ct);
            await serving;
            Assert.False(deployed.AlreadyPresent);
            Assert.Equal(RemoteHostServer.ReleaseVersion, await File.ReadAllTextAsync(Path.Combine(server.Home, ".dsharp", Rid, ".dsharp-version"), Ct));
            await using var connection = await SshHostLauncher.ConnectAsync(machine, deployed.CommandPath, "remote-download-test", Ct);
            Assert.Equal(harness, (await connection.Client.InfoAsync(Ct)).HomeRoot);
            await connection.Client.PingAsync(Ct);
            Assert.False(Directory.Exists(Path.Combine(server.Home, ".config", "systemd", "user")));
        }
        finally
        {
            transferCancellation.Cancel();
            RealHostProcesses.KillAll(harness);
            TempTree.Delete(harness);
        }
        guard.AssertUnchanged();
    }

    private static async Task ServeReleaseAsync(TcpListener listener, string archive, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        var request = await reader.ReadLineAsync(cancellationToken);
        Assert.Equal($"GET /releases/latest/download/{RemoteDsharpInstaller.AssetName(Rid)} HTTP/1.1", request);
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 })
        {
        }
        await using var file = File.OpenRead(archive);
        await using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Length: {file.Length}\r\nConnection: close\r\n\r\n");
            await writer.FlushAsync(cancellationToken);
        }
        await file.CopyToAsync(stream, cancellationToken);
    }

    /** The CLI of another harness home lists the remote session and diagnoses the machine through the same ssh path. */
    private static async Task AssertCliSeesTheMachineAsync(string executable, MachineSpec machine, string shell)
    {
        var home = RealHostProcesses.CreateHome("fk");
        try
        {
            new MachineStore(new HarnessHome(home)).Upsert(machine);
            var listed = await FleetCliProcess.RunAsync(executable, home, "session", "list", "--machine", machine.Name);
            Assert.True(listed.ExitCode == 0, listed.Error);
            Assert.Contains(FleetCliProcess.Rows(listed.Output), row => row is [var name, var id, HostSessionKinds.Terminal, ..] && name == machine.Name && id == shell);

            var doctor = await FleetCliProcess.RunAsync(executable, home, "doctor", "--machine", machine.Name);
            Assert.True(doctor.ExitCode == 0, doctor.Output);
            foreach (var check in new[] { "ssh", "platform", "dsharp", "host", "permissions", "connection" })
                Assert.Contains($"[ok] {check}:", doctor.Output, StringComparison.Ordinal);
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            TempTree.Delete(home);
        }
    }

    internal static string RequireEnvironment()
    {
        if (!File.Exists(Sshd) || TestSshServer.SftpServer is null)
            Assert.Skip($"needs an OpenSSH server ({Sshd} and sftp-server) to stand in for a POSIX remote machine");
        return RealHostProcesses.Executable;
    }

    /**
     * The release archive of this build: the output directory with only this platform's native runtimes (the others are most of its size).
     * Built once per test directory and kept, because a caller-provided archive is not deleted after deployment.
     */
    internal static async Task<string> BuildArchiveAsync(string executable, string rid, string root, CancellationToken cancellationToken)
    {
        Assert.Equal(Rid, rid);
        var archive = Path.Combine(root, RemoteDsharpInstaller.AssetName(rid));
        if (File.Exists(archive))
            return archive;
        var output = Path.GetDirectoryName(executable)!;
        await TestSshServer.RunAsync("tar", ["-cf", archive, "-C", output, "--exclude=./runtimes", "."], cancellationToken);
        await TestSshServer.RunAsync("tar", ["-rf", archive, "-C", output, $"./runtimes/{rid}"], cancellationToken);
        return archive;
    }

    /** The real ~/.ssh/known_hosts and ~/.dsharp must look the same after the test as before it. */
    internal sealed record RealHomeGuard(DateTime? KnownHosts, bool Deployed)
    {
        private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static RealHomeGuard Capture()
            => new(ModifiedAt(Path.Combine(UserHome, ".ssh", "known_hosts")), Directory.Exists(Path.Combine(UserHome, ".dsharp")));

        public void AssertUnchanged()
        {
            Assert.Equal(KnownHosts, ModifiedAt(Path.Combine(UserHome, ".ssh", "known_hosts")));
            Assert.Equal(Deployed, Directory.Exists(Path.Combine(UserHome, ".dsharp")));
        }

        private static DateTime? ModifiedAt(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }
}

/**
 * A user-mode OpenSSH server for one test: host and client keys, authorized_keys and known_hosts all live in the test directory.
 * Sessions get HOME=<test>/home and start there (ForceCommand), keeping deployment and host state in the test directory.
 */
internal sealed class TestSshServer : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    /**
     * scp speaks SFTP since OpenSSH 9.0, and ForceCommand wraps subsystem requests too, so the sftp subsystem must be a program the forced shell can run (internal-sftp cannot).
     * Paths of the OpenSSH packages of Debian/Ubuntu, Fedora, macOS and Arch.
     */
    internal static readonly string? SftpServer = ((string[])["/usr/lib/openssh/sftp-server", "/usr/libexec/openssh/sftp-server", "/usr/libexec/sftp-server", "/usr/lib/ssh/sftp-server"])
        .FirstOrDefault(File.Exists);

    private readonly Process _process;
    private readonly int _port;

    private TestSshServer(string root, Process process, int port)
    {
        Root = root;
        _process = process;
        _port = port;
    }

    public string Root { get; }

    public string Home => Path.Combine(Root, "home");

    private string ClientKey => Path.Combine(Root, "client_key");

    public static async Task<TestSshServer> StartAsync(CancellationToken cancellationToken)
    {
        var root = TempTree.CreateDirectory("sshd");
        Directory.CreateDirectory(Path.Combine(root, "home"));
        await RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(root, "host_key")], cancellationToken);
        await RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(root, "client_key")], cancellationToken);
        File.Copy(Path.Combine(root, "client_key.pub"), Path.Combine(root, "authorized_keys"));
        var port = FreePort();
        var config = Path.Combine(root, "sshd_config");
        await File.WriteAllTextAsync(config, Config(root, port), cancellationToken);
        var process = Process.Start(new ProcessStartInfo("/usr/sbin/sshd", ["-D", "-e", "-f", config])
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("failed to start sshd");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        var server = new TestSshServer(root, process, port);
        await server.WaitListeningAsync(cancellationToken);
        return server;
    }

    /** A machine for this server: key authentication with the test key, the test known_hosts, and no connection sharing through the real ~/.ssh. */
    public MachineSpec Machine(string name, string harnessHome)
        => MachineSpec.Ssh(name, IPAddress.Loopback.ToString(), _port, Environment.UserName, SshAuth.Key) with
        {
            KeyPath = ClientKey,
            HarnessHome = harnessHome,
            SshOptions = [$"UserKnownHostsFile={Path.Combine(Root, "known_hosts")}", "GlobalKnownHostsFile=/dev/null", "ControlMaster=no", "ControlPath=none"],
        };

    public static async Task RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, RedirectStandardError = true })
            ?? throw new InvalidOperationException($"failed to start {fileName}");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} failed ({process.ExitCode}): {await error}");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose();
        TempTree.Delete(Root);
    }

    private static string Config(string root, int port) => $"""
        Port {port}
        ListenAddress 127.0.0.1
        HostKey {Path.Combine(root, "host_key")}
        AuthorizedKeysFile {Path.Combine(root, "authorized_keys")}
        PidFile {Path.Combine(root, "sshd.pid")}
        UsePAM no
        StrictModes no
        PasswordAuthentication no
        KbdInteractiveAuthentication no
        PubkeyAuthentication yes
        Subsystem sftp {SftpServer}
        SetEnv HOME={Path.Combine(root, "home")} DOTNET_ROOT={DotnetRoot}
        ForceCommand cd "$HOME" && exec "$SHELL" -c "$SSH_ORIGINAL_COMMAND"
        LogLevel ERROR
        """;

    /** The .NET installation running this test (<root>/shared/Microsoft.NETCore.App/<version>), which the framework-dependent build needs on the "remote" side too. */
    private static string DotnetRoot => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task WaitListeningAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, _port, cancellationToken);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline && !_process.HasExited)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}
