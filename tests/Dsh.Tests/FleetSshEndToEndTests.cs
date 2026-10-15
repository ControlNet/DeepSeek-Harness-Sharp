using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * Dsh.Fleet over real ssh: a user-mode sshd on a loopback port (test-only keys, AuthorizedKeysFile in the test directory, UsePAM no) stands in for a remote machine.
 * The sshd gives sessions a test HOME and starts them there, so deployment lands in the test directory exactly as it would in a real home; nothing under the real ~/.ssh or ~/.dsharp is touched.
 * Covers automatic deployment, the nohup fallback without a systemd user manager, `host --serve --stdio`, listing sessions, reconnecting after the ssh process dies, and the CLI on that machine.
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
            var steps = new List<string>();
            connection.StateChanged += changed =>
            {
                if (changed is { State: MachineConnectionState.Deploying, Detail: { } step })
                    lock (steps)
                        steps.Add(step);
            };
            connection.Start();
            using (var settle = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                settle.CancelAfter(SettleTimeout);
                await connection.WaitSettledAsync(settle.Token);
            }

            Assert.True(connection.State == MachineConnectionState.Connected, connection.Detail);
            Assert.Contains(MachineConnectionState.Deploying, states);
            // Without a systemd user manager, `host install-service` falls back to a detached host.
            Assert.Contains(steps, step => step.StartsWith("host install-service:", StringComparison.Ordinal) && step.Contains("nohup", StringComparison.Ordinal));
            var deployed = Path.Combine(server.Home, ".dsharp", Rid);
            Assert.Equal(RemoteHostServer.ReleaseVersion, await File.ReadAllTextAsync(Path.Combine(deployed, ".dsharp-version"), Ct));
            Assert.Equal(harness, connection.Info!.HomeRoot);
            Assert.True(File.Exists(Path.Combine(harness, "run", "host.sock")), "install-service must have started the host under the machine's harness home");

            var reused = await RemoteDsharpInstaller.EnsureAsync(machine, options.Deploy, null, Ct, options.ArchiveFactory);
            Assert.True(reused.AlreadyPresent);
            Assert.Equal(Path.Combine(deployed, "dsharp"), reused.CommandPath);

            var lease = await connection.WaitConnectedAsync(Ct);
            var shell = await lease.Client.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", [], harness, Title: "e2e-shell"), Ct);
            await FleetConnectionTests.WaitForAsync(() => connection.Sessions.Any(session => session.SessionId == shell));

            var ssh = connection.LinkProcess!;
            ssh.Kill();
            await FleetConnectionTests.WaitForAsync(() => states.Contains(MachineConnectionState.Reconnecting)
                && connection.State == MachineConnectionState.Connected && connection.LinkProcess is { } next && next != ssh);
            Assert.Contains(connection.Sessions, session => session.SessionId == shell && session.Kind == HostSessionKinds.Terminal);

            await AssertCliSeesTheMachineAsync(executable, machine, shell);
        }
        finally
        {
            RealHostProcesses.KillAll(harness);
            TempTree.Delete(harness);
        }

        guard.AssertUnchanged();
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
            Assert.Contains("[skipped] service:", doctor.Output, StringComparison.Ordinal);
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
 * Sessions get HOME=<test>/home, start in it (ForceCommand) and see no systemd user manager (XDG_RUNTIME_DIR points nowhere), like a fresh machine without one.
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
        SetEnv HOME={Path.Combine(root, "home")} DOTNET_ROOT={DotnetRoot} XDG_RUNTIME_DIR={Path.Combine(root, "no-runtime")}
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
