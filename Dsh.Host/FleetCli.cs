using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Pty;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Host;

/**
 * `dsharp machine | session | doctor`: the machine list and the sessions of every machine from the command line.
 * Everything goes through Dsh.Fleet, so the CLI reads and writes the same machine list and connects the same way as the GUI.
 */
internal static class FleetCli
{
    private const string MachineCommand = "machine";
    private const string SessionCommand = "session";
    private const string DoctorCommand = "doctor";

    private static readonly HashSet<string> Flags = ["--local", "--force", "--password-stdin"];

    private static readonly HashSet<string> ValueOptions =
        ["--host", "--port", "--user", "--auth", "--key", "--proxy", "--remote-path", "--remote-home", "--ssh-option", "--home", "--machine"];

    public static bool Handles(string[] args) => args is [MachineCommand or SessionCommand or DoctorCommand, ..];

    /** resumeHarness opens a harness session of the local machine in this terminal's TUI. */
    public static async Task<int> RunAsync(HarnessHome home, string[] args, Func<HarnessHome, string, Task<int>> resumeHarness)
    {
        var parsed = FleetArguments.Parse(args.Skip(1), Flags, ValueOptions);
        if (parsed.Error is { } error)
            return await FailAsync(error);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        var context = new FleetCliContext(home, new MachineStore(home), parsed, resumeHarness, cancellation.Token);
        try
        {
            return args[0] switch
            {
                MachineCommand => await RunMachineAsync(context),
                SessionCommand => await RunSessionAsync(context),
                _ => await RunDoctorAsync(context),
            };
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException or NotSupportedException or JsonRpcException)
        {
            return await FailAsync(failure.Message);
        }
    }

    public static string Usage => """
               dsharp machine list
               dsharp machine add <name> --host <h> [--port N] [--user U] [--auth agent|key|password] [--key <path>] [--proxy <cmd>]
                                         [--remote-path <dir>] [--remote-home <dir>] [--ssh-option <o>]... [--password-stdin]
               dsharp machine add <name> --local --home <harness home>
               dsharp machine remove <name>
               dsharp machine test <name>               (connect without deploying)
               dsharp machine deploy <name> [--force]   (install or upgrade dsharp on the machine)
               dsharp session list [--machine <name>]
               dsharp session attach <machine>/<session>   (terminal sessions; leave with Ctrl+])
               dsharp session stop <machine>/<session>
               dsharp doctor [--machine <name>]
        """;

    private static Task<int> RunMachineAsync(FleetCliContext context) => context.Arguments.Subcommand switch
    {
        "list" or null => ListMachinesAsync(context),
        "add" => AddMachineAsync(context),
        "remove" => RemoveMachineAsync(context),
        "test" => TestMachineAsync(context),
        "deploy" => DeployMachineAsync(context),
        var other => FailAsync($"unknown machine subcommand \"{other}\" (list | add | remove | test | deploy)"),
    };

    private static Task<int> RunSessionAsync(FleetCliContext context) => context.Arguments.Subcommand switch
    {
        "list" or null => ListSessionsAsync(context),
        "attach" => AttachSessionAsync(context),
        "stop" => StopSessionAsync(context),
        var other => FailAsync($"unknown session subcommand \"{other}\" (list | attach | stop)"),
    };

    private static async Task<int> ListMachinesAsync(FleetCliContext context)
    {
        var rows = context.Store.Load().AllMachines()
            .Select(machine => new[] { machine.Name, machine.Kind, machine.Target, machine.IsLocal ? "-" : machine.Auth, machine.HarnessHome ?? "(default)" })
            .ToList();
        await WriteTableAsync(["NAME", "KIND", "TARGET", "AUTH", "HARNESS HOME"], rows);
        return 0;
    }

    private static async Task<int> AddMachineAsync(FleetCliContext context)
    {
        var arguments = context.Arguments;
        if (arguments.Operand is not { } name)
            return await FailAsync("machine add requires a name");
        var machine = arguments.Has("--local")
            ? MachineSpec.Local(arguments.Value("--home") is { } home ? Path.GetFullPath(home) : throw new InvalidOperationException("machine add --local requires --home <harness home>"), name)
            : await SshMachineAsync(name, arguments);
        context.Store.Upsert(machine);
        await Console.Out.WriteLineAsync($"saved {machine.Name} ({machine.Target}) to {context.Store.FilePath}");
        return 0;
    }

    private static async Task<MachineSpec> SshMachineAsync(string name, FleetArguments arguments)
    {
        var host = arguments.Value("--host") ?? throw new InvalidOperationException("machine add requires --host (or --local --home)");
        var port = arguments.Value("--port") is { } text
            ? int.TryParse(text, out var parsed) ? parsed : throw new InvalidOperationException($"invalid --port \"{text}\"")
            : MachineSpec.DefaultSshPort;
        var key = arguments.Value("--key");
        var auth = arguments.Value("--auth") ?? (key is not null ? SshAuth.Key : arguments.Has("--password-stdin") ? SshAuth.Password : SshAuth.Agent);
        if (auth is not (SshAuth.Agent or SshAuth.Key or SshAuth.Password))
            throw new InvalidOperationException($"invalid --auth \"{auth}\" (agent | key | password)");
        var password = arguments.Has("--password-stdin") ? await Console.In.ReadLineAsync() : null;
        return MachineSpec.Ssh(name, host, port, arguments.Value("--user") ?? Environment.UserName, auth) with
        {
            KeyPath = key is null ? null : Path.GetFullPath(key),
            Proxy = arguments.Value("--proxy"),
            RemotePath = arguments.Value("--remote-path"),
            HarnessHome = arguments.Value("--remote-home"),
            Password = password,
            SshOptions = arguments.Values("--ssh-option") is { Count: > 0 } options ? options : null,
        };
    }

    private static async Task<int> RemoveMachineAsync(FleetCliContext context)
    {
        if (context.Arguments.Operand is not { } name)
            return await FailAsync("machine remove requires a name");
        if (!context.Store.Remove(name))
            return await FailAsync($"no machine named \"{name}\"");
        await Console.Out.WriteLineAsync($"removed {name}");
        return 0;
    }

    /** Connects once without deploying or starting anything and reports what answered. */
    private static async Task<int> TestMachineAsync(FleetCliContext context)
    {
        var machine = context.RequireMachine(context.Arguments.Operand);
        await using var connection = new MachineConnection(machine, context.Options(deploy: false, startLocalHost: false));
        connection.Start();
        await connection.WaitSettledAsync(context.Cancellation);
        if (connection.State != MachineConnectionState.Connected)
            return await FailAsync($"{machine.Name} ({machine.Target}): {connection.Detail}");
        var info = connection.Info!;
        await Console.Out.WriteLineAsync($"{machine.Name} ({machine.Target}): host {info.HostVersion} on {info.Platform}, harness home {info.HomeRoot}, {connection.Sessions.Count} session(s)");
        return 0;
    }

    private static async Task<int> DeployMachineAsync(FleetCliContext context)
    {
        var machine = context.RequireMachine(context.Arguments.Operand);
        if (machine.IsLocal)
            return await FailAsync($"{machine.Name} is this computer; it runs this dsharp and needs no deployment");
        var deploy = context.Store.Load().Deploy;
        var result = await RemoteDsharpInstaller.EnsureAsync(
            machine, deploy, step => Console.Error.WriteLine(step), context.Cancellation, force: context.Arguments.Has("--force"));
        await Console.Out.WriteLineAsync(result.AlreadyPresent
            ? $"{machine.Name}: {result.CommandPath} is already at {RemoteHostServer.ReleaseVersion} or newer (use --force to redeploy)"
            : $"{machine.Name}: deployed {RemoteHostServer.ReleaseVersion} to {result.CommandPath}");
        return 0;
    }

    private static async Task<int> ListSessionsAsync(FleetCliContext context)
    {
        await using var fleet = await context.ConnectAsync(context.SelectedMachines());
        var pending = fleet.Pending().CountBy(item => (item.Machine, item.SessionId)).ToDictionary();
        var rows = fleet.Sessions()
            .Select(item => new[]
            {
                item.Machine, item.Session.SessionId, item.Session.Kind, item.Session.AgentKind ?? "-", item.Session.Status,
                Math.Max(item.Session.PendingCount, pending.GetValueOrDefault((item.Machine, item.Session.SessionId))).ToString(),
                item.Session.ProjectPath ?? "-",
            })
            .ToList();
        await WriteTableAsync(["MACHINE", "SESSION", "TYPE", "AGENT", "STATUS", "PENDING", "PROJECT"], rows);
        return await ReportUnreachableAsync(fleet);
    }

    private static async Task<int> AttachSessionAsync(FleetCliContext context)
    {
        var (connection, session) = await context.FindSessionAsync();
        await using (connection)
        {
            var lease = await connection.WaitConnectedAsync(context.Cancellation);
            if (session.Kind is HostSessionKinds.Terminal or HostSessionKinds.External)
                return await AttachTerminalAsync(lease.Client, connection.Machine, session, context.Cancellation);
            if (session.Kind != HostSessionKinds.Harness)
                return await FailAsync($"{session.Kind} sessions have no terminal to attach to; open them in the GUI");
            if (!connection.Machine.IsLocal)
                return await FailAsync($"harness sessions of {connection.Machine.Name} open in the GUI (dsharp gui); the command line attaches terminal sessions only");
        }

        return await context.ResumeHarness(context.HomeOf(connection.Machine), session.SessionId);
    }

    private static async Task<int> AttachTerminalAsync(RemoteHostClient client, MachineSpec machine, RemoteHostSession session, CancellationToken cancellationToken)
    {
        await Console.Out.WriteLineAsync($"dsharp: attaching to {machine.Name}/{session.SessionId}; leave with {ConsoleTunnel.DetachKeyName} (the session keeps running)");
        await ConsoleTunnel.RunAsync(
            (input, output, token) => client.AttachPtyAsync(session.SessionId, input, output, token),
            (columns, rows, token) => client.ResizePtyAsync(session.SessionId, columns, rows, token),
            cancellationToken);
        await Console.Out.WriteLineAsync($"dsharp: detached from {machine.Name}/{session.SessionId}");
        return 0;
    }

    private static async Task<int> StopSessionAsync(FleetCliContext context)
    {
        var (connection, session) = await context.FindSessionAsync();
        await using (connection)
        {
            var client = (await connection.WaitConnectedAsync(context.Cancellation)).Client;
            var target = $"{connection.Machine.Name}/{session.SessionId}";
            switch (session.Kind)
            {
                case HostSessionKinds.Terminal:
                    await client.StopPtyAsync(session.SessionId, context.Cancellation);
                    await Console.Out.WriteLineAsync($"stopped {target}");
                    return 0;
                case HostSessionKinds.Harness:
                    await client.InterruptAsync(session.SessionId, context.Cancellation);
                    await Console.Out.WriteLineAsync($"interrupted the running turn of {target}");
                    return 0;
                default:
                    return await FailAsync($"stopping {session.Kind} sessions needs the host's agent methods (host.agent.*), which the host of {connection.Machine.Name} does not offer yet");
            }
        }
    }

    private static async Task<int> RunDoctorAsync(FleetCliContext context)
    {
        var failed = false;
        foreach (var machine in context.SelectedMachines())
        {
            await Console.Out.WriteLineAsync($"{machine.Name} ({machine.Target})");
            var checks = await FleetDoctor.CheckAsync(machine, context.Options(deploy: false, startLocalHost: false), context.Cancellation);
            foreach (var check in checks)
            {
                await Console.Out.WriteLineAsync($"  [{check.Outcome.ToString().ToLowerInvariant()}] {check.Name}: {check.Detail}");
                if (check.Fix is { } fix)
                    await Console.Out.WriteLineAsync($"      fix: {fix}");
            }

            failed |= checks.Any(check => check.Outcome == DoctorOutcome.Failed);
        }

        return failed ? 1 : 0;
    }

    private static async Task<int> ReportUnreachableAsync(FleetClient fleet)
    {
        var offline = fleet.Statuses().Where(status => status.State != MachineConnectionState.Connected).ToList();
        foreach (var status in offline)
            await Console.Error.WriteLineAsync($"unreachable: {status.Machine} ({status.Target}): {status.Detail}");
        return offline.Count == 0 ? 0 : 1;
    }

    private static async Task WriteTableAsync(string[] header, List<string[]> rows)
    {
        var widths = header.Select((title, column) => rows.Select(row => row[column].Length).Append(title.Length).Max()).ToArray();
        foreach (var row in rows.Prepend(header))
            await Console.Out.WriteLineAsync(string.Join("  ", row.Select((cell, column) => cell.PadRight(widths[column]))).TrimEnd());
    }

    private static async Task<int> FailAsync(string message)
    {
        await Console.Error.WriteLineAsync($"dsharp: {message}");
        return 1;
    }
}

/** Positional arguments, flags and repeatable value options of one fleet command; Error is set for unknown or incomplete options. */
internal sealed record FleetArguments(List<string> Positional, HashSet<string> Flags, Dictionary<string, List<string>> Options, string? Error)
{
    public string? Subcommand => Positional.FirstOrDefault();

    public string? Operand => Positional.Skip(1).FirstOrDefault();

    public bool Has(string flag) => Flags.Contains(flag);

    public string? Value(string option) => Options.GetValueOrDefault(option)?.LastOrDefault();

    public List<string> Values(string option) => Options.GetValueOrDefault(option) ?? [];

    public static FleetArguments Parse(IEnumerable<string> args, HashSet<string> knownFlags, HashSet<string> valueOptions)
    {
        var parsed = new FleetArguments([], new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, List<string>>(StringComparer.Ordinal), null);
        using var cursor = args.GetEnumerator();
        while (cursor.MoveNext())
        {
            var argument = cursor.Current;
            if (knownFlags.Contains(argument))
                parsed.Flags.Add(argument);
            else if (valueOptions.Contains(argument))
            {
                if (!cursor.MoveNext())
                    return parsed with { Error = $"{argument} requires a value" };
                parsed.Options.TryAdd(argument, []);
                parsed.Options[argument].Add(cursor.Current);
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal))
                return parsed with { Error = $"unknown option \"{argument}\" (try --help)" };
            else
                parsed.Positional.Add(argument);
        }

        return parsed;
    }
}

/** What every fleet command works with: the harness home and its machine list, the parsed arguments and the Ctrl+C token. */
internal sealed record FleetCliContext(
    HarnessHome Home,
    MachineStore Store,
    FleetArguments Arguments,
    Func<HarnessHome, string, Task<int>> ResumeHarness,
    CancellationToken Cancellation)
{
    /** The CLI connects once per command: no reconnects, the stored deployment preferences, and a local host started on demand like ssh starts a remote one. */
    public FleetOptions Options(bool deploy, bool startLocalHost)
    {
        var settings = Store.Load().Deploy;
        return new FleetOptions
        {
            ClientName = $"cli@{Environment.MachineName}",
            Deploy = deploy ? settings : settings with { AutoDeploy = false },
            AutoReconnect = false,
            StartLocalHost = startLocalHost ? StartLocalHostAsync : null,
        };
    }

    public MachineSpec RequireMachine(string? name)
        => name is null
            ? throw new InvalidOperationException("a machine name is required (see dsharp machine list)")
            : Store.Load().Find(name) ?? throw new InvalidOperationException($"no machine named \"{name}\" (see dsharp machine list)");

    /** --machine narrows to one machine; otherwise this computer and every stored machine. */
    public IReadOnlyList<MachineSpec> SelectedMachines()
        => Arguments.Value("--machine") is { } name ? [RequireMachine(name)] : Store.Load().AllMachines();

    /** Connects to the given machines and waits until each has connected or failed. */
    public async Task<FleetClient> ConnectAsync(IReadOnlyList<MachineSpec> machines)
    {
        var fleet = new FleetClient(machines, Options(deploy: true, startLocalHost: true));
        foreach (var machine in fleet.Machines)
            machine.StateChanged += ReportDeployment;
        fleet.Start();
        await fleet.WaitSettledAsync(Cancellation);
        return fleet;
    }

    /** Resolves `<machine>/<session>` on a connected machine; the caller owns the returned connection. */
    public async Task<(MachineConnection Connection, RemoteHostSession Session)> FindSessionAsync()
    {
        var target = Arguments.Operand ?? throw new InvalidOperationException("a target <machine>/<session> is required (see dsharp session list)");
        var cut = target.IndexOf('/');
        if (cut <= 0 || cut == target.Length - 1)
            throw new InvalidOperationException($"\"{target}\" is not <machine>/<session>");
        var machine = RequireMachine(target[..cut]);
        var connection = new MachineConnection(machine, Options(deploy: true, startLocalHost: true));
        connection.StateChanged += ReportDeployment;
        connection.Start();
        await connection.WaitSettledAsync(Cancellation);
        var sessionId = target[(cut + 1)..];
        var session = connection.Sessions.FirstOrDefault(item => item.SessionId == sessionId);
        if (connection.State == MachineConnectionState.Connected && session is not null)
            return (connection, session);
        var problem = connection.State == MachineConnectionState.Connected
            ? $"{machine.Name} has no session \"{sessionId}\" (see dsharp session list --machine {machine.Name})"
            : $"{machine.Name} ({machine.Target}) is unreachable: {connection.Detail}";
        await connection.DisposeAsync();
        throw new InvalidOperationException(problem);
    }

    public HarnessHome HomeOf(MachineSpec machine) => machine.HarnessHome is { } home ? HarnessHome.Resolve(home) : Home;

    private static Task StartLocalHostAsync(MachineSpec machine, CancellationToken cancellationToken)
    {
        HostAutostart.StartDetached(token: null, machine.HarnessHome);
        return Task.CompletedTask;
    }

    private static void ReportDeployment(MachineConnection machine)
    {
        if (machine.State == MachineConnectionState.Deploying && machine.Detail is { } step)
            Console.Error.WriteLine($"{machine.Machine.Name}: {step}");
    }
}
