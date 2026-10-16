using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Dsh.Boot;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Host;

/**
 * 让远端 host 常驻: `dsharp host install-service` / `uninstall-service`, 以及 `host --serve --stdio` 找不到 host 时把它拉起来。
 * 有 systemd 的 Linux 装成 systemd --user 服务(启用 linger 后开机即起、退出登录不停); 其余系统没有可用的用户级服务管理器, 退回 nohup 起一个脱离会话的 host(重启后不再自起)。
 */
public static class HostAutostart
{
    public const string UnitName = "dsharp-host.service";

    /** systemd 作为 PID 1 运行时才有这个目录(sd_booted 的判据)。 */
    private const string SystemdRuntimeDirectory = "/run/systemd/system";

    /** systemd 查找用户单元的配置根; 未设置时为 ~/.config。 */
    private const string ConfigHomeVariable = "XDG_CONFIG_HOME";

    /** 单次 systemctl/loginctl 调用的上限: 用户管理器卡住时不让 CLI 一直挂着。 */
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);


    /** Kept open before composition and registry loading, so competing starts cannot replace a live endpoint or read a stale registry. */
    public static FileStream? TryAcquireInstance(HarnessHome home)
    {
        Directory.CreateDirectory(home.RunPath);
        try
        {
            return new FileStream(Path.Combine(home.RunPath, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
    /** systemd 读取用户单元的位置。 */
    public static string UnitPath()
    {
        var configHome = Environment.GetEnvironmentVariable(ConfigHomeVariable);
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "systemd", "user", UnitName);
    }

    /**
     * 用户单元内容。KillMode=process: 停服务只停 host 进程, 它拉起的 PTY daemon 与终端继续运行, 下一个 host 接着托管。
     * Restart=on-failure: 崩溃自动拉起; 被正常停止(SIGTERM)时不拉起, 下一次 stdio 连接再按需启动。
     * harnessHome 非空时写进 DSH_HOME, 服务与安装它的 shell 用同一个 harness 根。
     */
    public static string RenderUnit(string executable, IReadOnlyList<string> arguments, string? harnessHome)
    {
        var unit = new StringBuilder();
        unit.AppendLine("[Unit]");
        unit.AppendLine("Description=DeepSeek Harness remote workspace host (dsharp host serve)");
        unit.AppendLine();
        unit.AppendLine("[Service]");
        unit.AppendLine("Type=simple");
        // 只有 ExecStart 展开 $VAR, 所以只在这里把 $ 加倍; Environment= 里的 $ 本来就是字面量。
        var command = new[] { executable }.Concat(arguments).Select(argument => QuoteForSystemd(argument.Replace("$", "$$", StringComparison.Ordinal)));
        unit.AppendLine($"ExecStart={string.Join(' ', command)}");
        unit.AppendLine("WorkingDirectory=%h");
        if (harnessHome is { Length: > 0 })
            unit.AppendLine($"Environment={QuoteForSystemd($"{HarnessHome.HomeEnv}={harnessHome}")}");
        unit.AppendLine("Restart=on-failure");
        unit.AppendLine("RestartSec=2");
        unit.AppendLine("KillMode=process");
        unit.AppendLine();
        unit.AppendLine("[Install]");
        unit.AppendLine("WantedBy=default.target");
        return unit.ToString();
    }

    /** 当前 dsharp 跑 `host serve` 的命令行: 框架依赖部署时进程是 dotnet, 要带上入口 dll。 */
    public static (string Executable, IReadOnlyList<string> Arguments) ServeCommand(string? token = null)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("failed to resolve the current executable to start the host daemon");
        List<string> arguments = [];
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        if (token is { Length: > 0 })
            arguments.AddRange(["--token", token]);
        arguments.AddRange(["host", "serve"]);
        return (executable, arguments);
    }

    /** stdio 桥接之前: host 不可达时, 装了服务就经 systemd 启动, 否则(或启动失败)nohup 起一个。 */
    public static async Task EnsureRunningAsync(HarnessHome home, string? token)
    {
        if (await RemoteHostListener.IsReachableAsync(home.Root, CancellationToken.None))
            return;
        if (!(File.Exists(UnitPath()) && await IsSystemdUserAvailableAsync()
            && (await SystemctlAsync("start", UnitName)).ExitCode == 0))
            StartDetached(token, home.Root);
        using var connection = await RemoteHostListener.ConnectAsync(home.Root, CancellationToken.None);
    }

    public static async Task<int> InstallAsync(HarnessHome home, TextWriter output)
    {
        if (!await IsSystemdUserAvailableAsync())
            return await StartWithoutServiceManagerAsync(home, output);
        if (await RemoteHostListener.IsReachableAsync(home.Root, CancellationToken.None))
            await output.WriteLineAsync("note: a dsharp host started outside systemd is already running; stop it so the service owns the endpoint, then run: systemctl --user restart dsharp-host");

        var (executable, arguments) = ServeCommand();
        var unitPath = UnitPath();
        Directory.CreateDirectory(Path.GetDirectoryName(unitPath)!);
        await File.WriteAllTextAsync(unitPath, RenderUnit(executable, arguments, Environment.GetEnvironmentVariable(HarnessHome.HomeEnv)));
        await output.WriteLineAsync($"wrote {unitPath}");

        string[][] steps = [["daemon-reload"], ["enable", "--now", UnitName]];
        foreach (var step in steps)
        {
            var (exitCode, text) = await SystemctlAsync(step);
            if (exitCode == 0)
                continue;
            await output.WriteLineAsync($"systemctl --user {string.Join(' ', step)} failed ({exitCode}): {text}");
            return 1;
        }
        await output.WriteLineAsync($"enabled and started {UnitName}");
        await EnableLingerAsync(output);
        return 0;
    }

    public static async Task<int> UninstallAsync(TextWriter output)
    {
        var unitPath = UnitPath();
        if (!File.Exists(unitPath))
        {
            await output.WriteLineAsync($"{UnitName} is not installed ({unitPath} does not exist)");
            return 0;
        }

        if (await IsSystemdUserAvailableAsync())
        {
            var (exitCode, text) = await SystemctlAsync("disable", "--now", UnitName);
            if (exitCode != 0)
                await output.WriteLineAsync($"systemctl --user disable --now {UnitName} failed ({exitCode}): {text}");
        }
        File.Delete(unitPath);
        if (await IsSystemdUserAvailableAsync())
            await SystemctlAsync("daemon-reload");
        await output.WriteLineAsync($"removed {unitPath}; linger is left as it was (disable it with: loginctl disable-linger)");
        return 0;
    }

    /**
     * 以脱离会话的方式起 `dsharp host serve`(ssh 断开不把 daemon 带走)。
     * harnessHome 非空时子进程用它作 DSH_HOME, 否则继承本进程的 harness 根。
     */
    public static void StartDetached(string? token, string? harnessHome = null)
    {
        var (executable, arguments) = ServeCommand(token);
        if (OperatingSystem.IsWindows())
        {
            // Without inherited handles: a host holding the CLI's output pipe kept `dsharp session list | ...` waiting for as long as the host ran.
            Dictionary<string, string?> environment = harnessHome is { Length: > 0 } ? new() { [HarnessHome.HomeEnv] = harnessHome } : [];
            WindowsProcessLaunch.StartWithoutInheritedHandles(executable, arguments, Directory.GetCurrentDirectory(), environment);
            return;
        }

        var quoted = string.Join(' ', arguments.Select(argument => $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'"));
        var info = new ProcessStartInfo("/bin/sh", ["-c", $"nohup '{executable}' {quoted} >/dev/null 2>&1 &"])
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        if (harnessHome is { Length: > 0 })
            info.Environment[HarnessHome.HomeEnv] = harnessHome;
        Process.Start(info);
    }

    private static async Task<int> StartWithoutServiceManagerAsync(HarnessHome home, TextWriter output)
    {
        await output.WriteLineAsync("no systemd user manager here; falling back to a detached (nohup) host that will not start again after a reboot");
        if (await RemoteHostListener.IsReachableAsync(home.Root, CancellationToken.None))
        {
            await output.WriteLineAsync("a dsharp host is already running");
            return 0;
        }
        StartDetached(null, home.Root);
        using var connection = await RemoteHostListener.ConnectAsync(home.Root, CancellationToken.None);
        await output.WriteLineAsync($"started a detached dsharp host; it listens on {RemoteHostEndpoint.SocketPath(home.Root)}");
        return 0;
    }

    /** linger 让用户管理器在开机时启动、在最后一次退出登录后保留; 没有权限时只给出提示, 不提权。 */
    private static async Task EnableLingerAsync(TextWriter output)
    {
        var user = Environment.UserName;
        var (_, linger) = await RunAsync("loginctl", "show-user", user, "--property=Linger", "--value");
        if (linger == "yes")
        {
            await output.WriteLineAsync("linger is already enabled: the host starts at boot");
            return;
        }
        var (exitCode, text) = await RunAsync("loginctl", "enable-linger", user);
        await output.WriteLineAsync(exitCode == 0
            ? "enabled linger: the host starts at boot and survives logout"
            : $"could not enable linger ({text}); the host runs only while you are logged in. An administrator can enable it with: sudo loginctl enable-linger {user}");
    }

    /** 机器由 systemd 引导, 且当前会话能连上自己的用户管理器(ssh 会话需要 XDG_RUNTIME_DIR)。 */
    private static async Task<bool> IsSystemdUserAvailableAsync()
        => OperatingSystem.IsLinux() && Directory.Exists(SystemdRuntimeDirectory)
            && (await SystemctlAsync("show-environment")).ExitCode == 0;

    private static Task<(int ExitCode, string Output)> SystemctlAsync(params string[] arguments)
        => RunAsync("systemctl", ["--user", .. arguments]);

    private static async Task<(int ExitCode, string Output)> RunAsync(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var timeout = new CancellationTokenSource(CommandTimeout);
        try
        {
            using var process = Process.Start(info) ?? throw new Win32Exception($"failed to start {fileName}");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, $"{await stdout}{await stderr}".Trim());
        }
        catch (Exception error) when (error is Win32Exception or OperationCanceledException)
        {
            return (-1, error.Message);
        }
    }

    /** systemd 引号: 整体加双引号, 反斜杠与双引号按 C 风格转义, % 加倍以免被当成说明符。 */
    private static string QuoteForSystemd(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
