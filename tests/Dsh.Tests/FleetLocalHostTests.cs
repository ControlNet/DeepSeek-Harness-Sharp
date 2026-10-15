using System.Diagnostics;
using System.Text;
using Dsh.Fleet;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * Dsh.Fleet and the fleet CLI against real `dsharp host serve` processes of this build, each under its own throwaway harness home.
 * Covers starting the local host on demand, reconnecting after the host process is killed, and `dsharp machine|session|doctor` end to end on two machines.
 */
public sealed class FleetLocalHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 120000)]
    public async Task LocalHost_IsStartedOnDemand_AndTheConnectionRecoversAfterTheHostIsKilled()
    {
        var executable = RealHostProcesses.Executable;
        var home = RealHostProcesses.CreateHome("fl");
        try
        {
            Process? host = null;
            var options = FleetConnectionTests.TestOptions with
            {
                StartLocalHost = (_, _) =>
                {
                    host = RealHostProcesses.StartHost(executable, home);
                    return Task.CompletedTask;
                },
            };
            await using var connection = new MachineConnection(MachineSpec.Local(home, "real"), options);
            connection.Start();
            await connection.WaitSettledAsync(TestContext.Current.CancellationToken);
            Assert.Equal(MachineConnectionState.Connected, connection.State);
            var first = Assert.IsType<Process>(host);
            Assert.Equal(RemoteHostServer.ReleaseVersion, connection.Info!.HostVersion);

            var ptyId = await StartShellAsync(connection, home);
            await FleetConnectionTests.WaitForAsync(() => connection.Sessions.Any(session => session is { Kind: HostSessionKinds.Terminal } && session.SessionId == ptyId));

            var states = FleetConnectionTests.RecordStates(connection);
            first.Kill();
            await FleetConnectionTests.WaitForAsync(() => states.Contains(MachineConnectionState.Reconnecting) && connection.State == MachineConnectionState.Connected);
            Assert.NotSame(first, host);
            await FleetConnectionTests.WaitForAsync(() => connection.Sessions.Any(session => session.SessionId == ptyId));
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            TempTree.Delete(home);
        }
    }

    /** Two machines (this harness home and a second local one), each with a real host the CLI starts on demand. */
    [Fact(Timeout = 180000)]
    public async Task Cli_ManagesMachines_ListsStopsAndDiagnosesSessions()
    {
        var executable = RealHostProcesses.Executable;
        var home = RealHostProcesses.CreateHome("fc");
        var second = RealHostProcesses.CreateHome("fd");
        try
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var added = await FleetCliProcess.RunAsync(executable, home, "machine", "add", "second", "--local", "--home", second);
            Assert.Equal(0, added.ExitCode);
            var machines = await FleetCliProcess.RunAsync(executable, home, "machine", "list");
            Assert.Contains(FleetCliProcess.Rows(machines.Output), row => row is ["second", MachineKinds.Local, var target, ..] && target == second);

            var empty = await FleetCliProcess.RunAsync(executable, home, "session", "list");
            Assert.True(empty.ExitCode == 0, empty.Error);
            var localShell = await StartShellAsync(home);
            var secondShell = await StartShellAsync(second);

            var listed = await FleetCliProcess.RunAsync(executable, home, "session", "list");
            Assert.Equal(0, listed.ExitCode);
            Assert.Contains(FleetCliProcess.Rows(listed.Output), row => row is ["local", var id, HostSessionKinds.Terminal, ..] && id == localShell);
            Assert.Contains(FleetCliProcess.Rows(listed.Output), row => row is ["second", var id, HostSessionKinds.Terminal, ..] && id == secondShell);
            var narrowed = await FleetCliProcess.RunAsync(executable, home, "session", "list", "--machine", "second");
            Assert.DoesNotContain(localShell, narrowed.Output, StringComparison.Ordinal);

            var doctor = await FleetCliProcess.RunAsync(executable, home, "doctor", "--machine", "second");
            Assert.True(doctor.ExitCode == 0, doctor.Output);
            Assert.Contains("[ok] connection:", doctor.Output, StringComparison.Ordinal);
            // The endpoint checks read the POSIX status script's output, which FleetDoctor does not run for a local Windows machine.
            if (!OperatingSystem.IsWindows())
            {
                Assert.Contains("[ok] host:", doctor.Output, StringComparison.Ordinal);
                Assert.Contains("[ok] permissions:", doctor.Output, StringComparison.Ordinal);
                Assert.Contains("[ok] pty daemon:", doctor.Output, StringComparison.Ordinal);
            }

            var stopped = await FleetCliProcess.RunAsync(executable, home, "session", "stop", $"second/{secondShell}");
            Assert.True(stopped.ExitCode == 0, stopped.Error);
            await WaitForSessionStatusAsync(second, secondShell, HostSessionStatuses.Exited);
            var missing = await FleetCliProcess.RunAsync(executable, home, "session", "stop", "second/no-such-session");
            Assert.Equal(1, missing.ExitCode);
            Assert.True(missing.Error.Contains("no session", StringComparison.Ordinal), missing.Error);

            Assert.Equal(0, (await FleetCliProcess.RunAsync(executable, home, "machine", "remove", "second")).ExitCode);
            Assert.DoesNotContain(secondShell, (await FleetCliProcess.RunAsync(executable, home, "session", "list")).Output, StringComparison.Ordinal);
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            RealHostProcesses.KillAll(second);
            TempTree.Delete(home);
            TempTree.Delete(second);
        }
    }

    /** `dsharp session attach` in a real terminal: keystrokes reach the host's shell, its output comes back, and Ctrl+] leaves the session running. */
    [Fact(Timeout = 120000)]
    public async Task Cli_AttachesToATerminalSession_AndDetachesWithTheEscapeKey()
    {
        var executable = RealHostProcesses.Executable;
        var home = RealHostProcesses.CreateHome("fa");
        try
        {
            RealHostProcesses.StartHost(executable, home);
            var shell = await StartShellAsync(home);
            using var terminal = new PtyHost();
            var session = await terminal.StartAsync(new PtyStartInfo
            {
                FileName = executable,
                Arguments = ["session", "attach", $"local/{shell}"],
                WorkingDirectory = home,
                Environment = new Dictionary<string, string?> { [RemoteShell.HomeVariable] = home },
            }, cancellationToken: TestContext.Current.CancellationToken);
            var transcript = new StringBuilder();
            var pump = PumpAsync(session, transcript);

            await WaitForTextAsync(transcript, "attaching to");
            await session.WriteAsync(Encoding.UTF8.GetBytes($"{TestShell.EchoAnswer("fleet")}\r"), Ct);
            await WaitForTextAsync(transcript, "fleet-42");
            await session.WriteAsync(new[] { ConsoleTunnel.DetachKey }, Ct);
            await WaitForTextAsync(transcript, "detached from");
            await pump;
            await WaitForSessionStatusAsync(home, shell, HostSessionStatuses.Running);
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            TempTree.Delete(home);
        }
    }

    private static async Task<string> StartShellAsync(MachineConnection connection, string cwd)
    {
        var lease = await connection.WaitConnectedAsync(Ct);
        return await lease.Client.StartPtyAsync(new RemotePtyStartRequest(TestShell.FileName, TestShell.Arguments, cwd, Title: "fleet-shell"), Ct);
    }

    /** Starts a shell on the host of a home whose host is already running. */
    private static async Task<string> StartShellAsync(string home)
    {
        await using var connection = new MachineConnection(MachineSpec.Local(home), FleetConnectionTests.TestOptions);
        connection.Start();
        await connection.WaitSettledAsync(Ct);
        return await StartShellAsync(connection, home);
    }

    private static async Task WaitForSessionStatusAsync(string home, string sessionId, string status)
    {
        await using var connection = new MachineConnection(MachineSpec.Local(home), FleetConnectionTests.TestOptions);
        connection.Start();
        await connection.WaitSettledAsync(Ct);
        try
        {
            await FleetConnectionTests.WaitForAsync(() => connection.Sessions.Any(session => session.SessionId == sessionId && session.Status == status));
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            var sessions = string.Join(", ", connection.Sessions.Select(session => $"{session.SessionId}={session.Status}"));
            Assert.Fail($"{sessionId} never became {status}; connection {connection.State} ({connection.Detail}); sessions: {sessions}");
        }
    }

    private static async Task PumpAsync(PtySession session, StringBuilder transcript)
    {
        var buffer = new byte[4096];
        while (session.Status == PtySessionStatus.Running)
        {
            int read;
            try
            {
                read = await session.ReadAsync(buffer, Ct);
            }
            catch (Exception) when (!Ct.IsCancellationRequested)
            {
                // The child exited (EIO on Unix).
                return;
            }

            if (read == 0)
            {
                await Task.Delay(5, Ct);
                continue;
            }

            lock (transcript)
                transcript.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    private static Task WaitForTextAsync(StringBuilder transcript, string text)
        => FleetConnectionTests.WaitForAsync(() =>
        {
            lock (transcript)
                return transcript.ToString().Contains(text, StringComparison.Ordinal);
        });
}
