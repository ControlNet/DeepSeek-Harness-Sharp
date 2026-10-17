using System.Diagnostics;
using System.Runtime.InteropServices;
using Dsh.RemoteHost;

namespace Dsh.Fleet;

public enum DoctorOutcome
{
    Ok,
    Warning,
    Failed,
    Skipped,
}

/** One diagnosis: what was checked, how it went, what was found, and a command that repairs it when it is not Ok. */
public sealed record DoctorCheck(string Name, DoctorOutcome Outcome, string Detail, string? Fix = null);

/**
 * Diagnoses one machine for `dsharp doctor`: ssh reachability, platform, the deployed dsharp and its release, whether the host runs, the service, endpoint permissions, the PTY daemon and a live host round trip.
 * The same POSIX status script runs over ssh for remote machines and through /bin/sh for the local one, so both report from the machine's own view.
 */
public static class FleetDoctor
{
    private const string OwnerOnlyDirectoryMode = "700";
    private const string OwnerOnlyFileMode = "600";
    private const string HostSocket = "host.sock";
    private const string PtySocket = "dsh.sock";
    private const string ModePrefix = "mode.";
    private const string LocalCommand = "dsharp";

    /** Endpoint files that must stay owner-only: the host's and the PTY daemon's socket and token. */
    private static readonly string[] EndpointFiles = [HostSocket, "host.token", PtySocket, "dsh.token"];

    public static async Task<IReadOnlyList<DoctorCheck>> CheckAsync(MachineSpec machine, FleetOptions options, CancellationToken cancellationToken)
    {
        List<DoctorCheck> checks = [];
        if (!machine.IsLocal)
        {
            var reach = await ReachAsync(machine, cancellationToken).ConfigureAwait(false);
            checks.Add(reach);
            if (reach.Outcome == DoctorOutcome.Failed)
                return checks;
        }

        var commandPath = await InstallationAsync(machine, checks, cancellationToken).ConfigureAwait(false);
        if (commandPath is null && !machine.IsLocal)
            return checks;
        var status = await StatusAsync(machine, cancellationToken).ConfigureAwait(false);
        checks.Add(HostCheck(machine, commandPath, status));
        checks.Add(ServiceCheck(machine, commandPath, status));
        checks.Add(PermissionCheck(machine, status));
        checks.Add(PtyDaemonCheck(status));
        checks.Add(await ConnectionAsync(machine, options, commandPath, status, cancellationToken).ConfigureAwait(false));
        return checks;
    }

    private static async Task<DoctorCheck> ReachAsync(MachineSpec machine, CancellationToken cancellationToken)
    {
        var result = await SshHostLauncher.RunRemoteCaptureAsync(machine, "true", cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != RemoteDsharpInstaller.SshFailureExitCode)
            return new DoctorCheck("ssh", DoctorOutcome.Ok, $"{machine.Target} accepts the connection ({machine.Auth})");
        var fix = !machine.IsIroh && machine.Auth == SshAuth.Key && machine.KeyPath is { Length: > 0 } key
            ? $"ssh-copy-id -p {machine.Port} -i {RemoteShell.Quote($"{key}.pub")} {machine.User}@{machine.Host}"
            : SshHostLauncher.CommandLine(machine, "true");
        return new DoctorCheck("ssh", DoctorOutcome.Failed, result.FailureDetail, fix);
    }

    /** Adds the platform and dsharp checks; returns the dsharp the machine would run, or null when there is none or the platform is unsupported. */
    private static async Task<string?> InstallationAsync(MachineSpec machine, List<DoctorCheck> checks, CancellationToken cancellationToken)
    {
        if (machine.IsLocal)
        {
            checks.Add(new DoctorCheck("platform", DoctorOutcome.Ok, $"{RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier})"));
            checks.Add(new DoctorCheck("dsharp", DoctorOutcome.Ok, $"{RemoteHostServer.ReleaseVersion} at {Environment.ProcessPath}"));
            return Environment.ProcessPath;
        }

        try
        {
            var installation = await RemoteDsharpInstaller.InspectAsync(machine, cancellationToken).ConfigureAwait(false);
            checks.Add(new DoctorCheck("platform", DoctorOutcome.Ok, $"{installation.Rid}, harness home {installation.HarnessHome}"));
            checks.Add(DsharpCheck(machine, installation));
            return installation.CommandPath;
        }
        catch (Exception error) when (error is NotSupportedException or IOException)
        {
            checks.Add(new DoctorCheck("platform", DoctorOutcome.Failed, error.Message));
            return null;
        }
    }

    private static DoctorCheck DsharpCheck(MachineSpec machine, RemoteDsharpInstaller.Installation installation)
    {
        var deploy = $"dsharp machine deploy {machine.Name}";
        if (installation.CommandPath is not { } path)
            return new DoctorCheck("dsharp", DoctorOutcome.Failed, "dsharp is not installed", deploy);
        var current = RemoteHostServer.ReleaseVersion;
        return ReleaseVersions.CanReuse(installation.Version, current)
            ? new DoctorCheck("dsharp", DoctorOutcome.Ok, $"{installation.Version} at {path}")
            : new DoctorCheck("dsharp", DoctorOutcome.Warning, $"{installation.Version ?? "unknown release"} at {path} is older than this client ({current})", deploy);
    }

    /** Runs the status script on the machine; a local Windows machine has no POSIX shell and reports nothing. */
    private static async Task<Dictionary<string, string>> StatusAsync(MachineSpec machine, CancellationToken cancellationToken)
    {
        if (machine.IsLocal && OperatingSystem.IsWindows())
            return [];
        var script = StatusScript(machine);
        var result = machine.IsLocal
            ? await RunLocalAsync(script, cancellationToken).ConfigureAwait(false)
            : await SshHostLauncher.RunRemoteCaptureAsync(machine, script, cancellationToken).ConfigureAwait(false);
        return RemoteDsharpInstaller.KeyValues(result.StdOut);
    }

    /** Prints key=value lines: the harness home, endpoint file modes, the service unit and the user manager's view of it. */
    internal static string StatusScript(MachineSpec machine)
        => RemoteShell.Prelude(machine)
            + RemoteShell.ResolveHome
            + RemoteShell.ResolveUnit
            + "m() { stat -c %a \"$1\" 2>/dev/null || stat -f %Lp \"$1\" 2>/dev/null; }; "
            + "echo \"home=$h\"; "
            + "[ -d \"$h/run\" ] && echo \"run=$(m \"$h/run\")\"; "
            + $"for f in {string.Join(' ', EndpointFiles)}; do [ -e \"$h/run/$f\" ] && echo \"{ModePrefix}$f=$(m \"$h/run/$f\")\"; done; "
            + "[ -f \"$u\" ] && echo unit=1; "
            + "if [ -d /run/systemd/system ] && systemctl --user show-environment >/dev/null 2>&1; then echo systemd=1; "
            + $"echo \"active=$(systemctl --user is-active {RemoteShell.ServiceUnit} 2>/dev/null)\"; "
            + "echo \"linger=$(loginctl show-user \"$(id -un)\" --property=Linger --value 2>/dev/null)\"; fi; "
            + "true";

    private static async Task<SshHostLauncher.RemoteCommandResult> RunLocalAsync(string script, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("/bin/sh", ["-c", script])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start /bin/sh");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new SshHostLauncher.RemoteCommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static DoctorCheck HostCheck(MachineSpec machine, string? commandPath, Dictionary<string, string> status)
    {
        var socket = $"{status.GetValueOrDefault("home")}/run/{HostSocket}";
        if (status.ContainsKey(ModePrefix + HostSocket))
            return new DoctorCheck("host", DoctorOutcome.Ok, $"listening on {socket}");
        var detail = machine.IsLocal
            ? $"no host is listening on {socket}"
            : $"no host is listening on {socket}; ssh connections start one on demand";
        return new DoctorCheck("host", DoctorOutcome.Warning, detail, Fix(machine, $"{DsharpCommand(machine, commandPath)} host install-service"));
    }

    private static DoctorCheck ServiceCheck(MachineSpec machine, string? commandPath, Dictionary<string, string> status)
    {
        if (!status.ContainsKey("systemd"))
            return new DoctorCheck("service", DoctorOutcome.Skipped, "no systemd user manager: the host runs detached (nohup) and does not start again after a reboot");
        if (!status.ContainsKey("unit"))
            return new DoctorCheck("service", DoctorOutcome.Warning, $"{RemoteShell.ServiceUnit} is not installed", Fix(machine, $"{DsharpCommand(machine, commandPath)} host install-service"));
        if (status.GetValueOrDefault("active") != "active")
            return new DoctorCheck("service", DoctorOutcome.Warning, $"{RemoteShell.ServiceUnit} is {status.GetValueOrDefault("active")}",
                Fix(machine, $"systemctl --user restart {RemoteShell.ServiceUnit}"));
        if (status.GetValueOrDefault("linger") != "yes")
        {
            var user = machine.IsLocal ? Environment.UserName : machine.User;
            return new DoctorCheck("service", DoctorOutcome.Warning, $"{RemoteShell.ServiceUnit} is active, but linger is off: it stops when {user} logs out", Fix(machine, $"sudo loginctl enable-linger {user}"));
        }

        return new DoctorCheck("service", DoctorOutcome.Ok, $"{RemoteShell.ServiceUnit} is active and starts at boot");
    }

    private static DoctorCheck PermissionCheck(MachineSpec machine, Dictionary<string, string> status)
    {
        if (status.GetValueOrDefault("run") is not { } runMode)
            return new DoctorCheck("permissions", DoctorOutcome.Skipped, "no endpoint directory yet");
        var run = $"{status.GetValueOrDefault("home")}/run";
        var loose = EndpointFiles
            .Where(file => status.TryGetValue(ModePrefix + file, out var mode) && mode != OwnerOnlyFileMode)
            .Select(file => $"{run}/{file}")
            .ToList();
        if (runMode == OwnerOnlyDirectoryMode && loose.Count == 0)
            return new DoctorCheck("permissions", DoctorOutcome.Ok, $"{run} is {OwnerOnlyDirectoryMode}, sockets and tokens are {OwnerOnlyFileMode}");
        var fix = $"chmod {OwnerOnlyDirectoryMode} {RemoteShell.Quote(run)}";
        if (loose.Count > 0)
            fix += $" && chmod {OwnerOnlyFileMode} {string.Join(' ', loose.Select(RemoteShell.Quote))}";
        return new DoctorCheck("permissions", DoctorOutcome.Failed, $"{run} is {runMode}; open to others: {(loose.Count == 0 ? "-" : string.Join(", ", loose))}", Fix(machine, fix));
    }

    private static DoctorCheck PtyDaemonCheck(Dictionary<string, string> status)
        => status.ContainsKey(ModePrefix + PtySocket)
            ? new DoctorCheck("pty daemon", DoctorOutcome.Ok, $"listening on {status.GetValueOrDefault("home")}/run/{PtySocket}")
            : new DoctorCheck("pty daemon", DoctorOutcome.Skipped, "not running; it starts with the first terminal session");

    /** A live round trip: info and ping through the same path a client uses, and the host's release against this client's. */
    private static async Task<DoctorCheck> ConnectionAsync(MachineSpec machine, FleetOptions options, string? commandPath, Dictionary<string, string> status, CancellationToken cancellationToken)
    {
        if (machine.IsLocal && !await RemoteHostListener.IsReachableAsync(HostLink.LocalRoot(machine), cancellationToken).ConfigureAwait(false))
            return new DoctorCheck("connection", DoctorOutcome.Skipped, "the host is not running");
        try
        {
            await using var link = machine.IsLocal
                ? await HostLink.OpenLocalAsync(machine, options with { StartLocalHost = null }, cancellationToken).ConfigureAwait(false)
                : await HostLink.OpenSshAsync(machine, commandPath, options, cancellationToken).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            await link.Client.PingAsync(cancellationToken).ConfigureAwait(false);
            var info = await link.Client.InfoAsync(cancellationToken).ConfigureAwait(false);
            return VersionCheck(machine, info, clock.Elapsed, status);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return new DoctorCheck("connection", DoctorOutcome.Failed, error.Message, Fix(machine, $"{DsharpCommand(machine, commandPath)} host serve"));
        }
    }

    private static DoctorCheck VersionCheck(MachineSpec machine, HostInfo info, TimeSpan ping, Dictionary<string, string> status)
    {
        var detail = $"host {info.HostVersion} on {info.Platform}, protocol {info.ProtocolVersion}, ping {ping.TotalMilliseconds:F0} ms";
        var current = RemoteHostServer.ReleaseVersion;
        if (ReleaseVersions.CanReuse(info.HostVersion, current))
            return new DoctorCheck("connection", DoctorOutcome.Ok, detail);
        string? fix = machine.IsLocal
            ? status.ContainsKey("unit") ? $"systemctl --user restart {RemoteShell.ServiceUnit}" : null
            : $"dsharp machine deploy {machine.Name} --force";
        return new DoctorCheck("connection", DoctorOutcome.Warning, $"{detail}; older than this client ({current}), restart it to load the new release", fix);
    }

    /** The repair runs on the machine with its harness home: directly for the local one, wrapped in an ssh command line for a remote one. */
    private static string Fix(MachineSpec machine, string command)
        => machine.IsLocal
            ? $"{RemoteShell.HomeAssignment(machine.HarnessHome)}{command}"
            : SshHostLauncher.CommandLine(machine, $"{RemoteShell.Prelude(machine)}{command}");

    private static string DsharpCommand(MachineSpec machine, string? commandPath)
        => machine.IsLocal || commandPath is null ? LocalCommand : RemoteShell.Quote(commandPath);
}
