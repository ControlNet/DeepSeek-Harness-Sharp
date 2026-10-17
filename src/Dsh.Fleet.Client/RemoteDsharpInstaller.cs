using System.Text.RegularExpressions;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Fleet;

/**
 * 远端 dsharp 自动部署(仿 VS Code Remote-SSH 的 VS Code Server):
 * 远端没有 dsharp 或部署的发布版本旧于本客户端时, 取对应平台的 Release 构建解到 `~/.dsharp/RID/`, 之后按绝对路径运行。
 * 不注册任何 PATH 命令: 客户端直接用 `~/.dsharp/RID/dsharp host --serve --stdio` 起宿主。
 * 部署只提供二进制; 首次连接通过 stdio 桥按需启动独立宿主, 不安装系统服务。
 * 只支持 POSIX 远端(Linux/macOS); Windows 远端明确报不支持。
 */
public static class RemoteDsharpInstaller
{
    /** OpenSSH 自身失败(连接/认证/端口不通)的退出码固定为 255; 远端命令自身的失败是别的退出码。 */
    internal const int SshFailureExitCode = 255;

    /**
     * 部署身份 = 本项目的发布版本(Directory.Build.props 的 Version, 经 RemoteHostServer.ReleaseVersion 读出), 写在 `~/.dsharp/RID/.dsharp-version`。
     * 远端版本不旧于本客户端即复用(ReleaseVersions.CanReuse); 纯本地比较, 不联网、不看提交号。
     */
    private const string VersionFileName = ".dsharp-version";

    /** 远端没有 curl 也没有 wget 时下载脚本的退出码, auto 模式据此回退到本地下发。 */
    private const int NoDownloaderExitCode = 42;

    /** 停掉旧宿主后等它退出的轮询: 最多 50 次, 每次 0.2 秒。 */
    private const int StopWaitAttempts = 50;

    public const string WindowsUnsupported =
        "远端为 Windows: 暂不支持自动部署。请在远端手动安装 dsharp, 并在机器的“远端目录”里指向它所在的目录。";

    /** 部署结果: CommandPath 为远端 dsharp 的绝对路径(可交给启动器当 hostCommand)。 */
    public sealed record Result(string CommandPath, bool AlreadyPresent);

    /** 远端现状: 平台、解析出的 harness home、找到的 dsharp(优先本工具的部署目录)及其部署版本标记。 */
    public sealed record Installation(string Rid, string HarnessHome, string? CommandPath, string? Version);

    /**
     * 确保远端有不旧于本客户端的 dsharp: 可复用就返回其路径, 否则下载并部署对应平台构建。
     * archiveFactory 可注入本地归档(离线部署/测试); 为空则从 Release 下载。force 为真时无论版本都重新部署。
     */
    public static async Task<Result> EnsureAsync(
        MachineSpec machine,
        DeploySettings settings,
        Action<string>? progress,
        CancellationToken cancellationToken,
        Func<DeploySettings, string, CancellationToken, Task<string>>? archiveFactory = null,
        bool force = false,
        OutboundProxy? localProxy = null)
    {
        var installation = await InspectAsync(machine, cancellationToken).ConfigureAwait(false);
        var identity = RemoteHostServer.ReleaseVersion;
        if (!force && installation.CommandPath is { } present && ReleaseVersions.CanReuse(installation.Version, identity))
            return new Result(present, true);

        progress?.Invoke(installation.CommandPath is null
            ? $"远端缺少 dsharp, 正在部署 {identity}({installation.Rid}) …"
            : $"远端 dsharp 版本 {installation.Version ?? "(未知)"} 需要更新到 {identity}, 重新部署 …");
        var stop = StopHostCommand(installation.CommandPath);
        var deployed = await DeployAsync(machine, settings, installation.Rid, stop, progress, archiveFactory, localProxy, cancellationToken).ConfigureAwait(false);
        await WriteIdentityAsync(machine, installation.Rid, identity, cancellationToken).ConfigureAwait(false);
        return new Result(deployed, false);
    }

    /** 只探测不部署: 识别平台(Windows 抛 NotSupportedException), 解析 harness home, 找出 dsharp 与其部署版本。 */
    public static async Task<Installation> InspectAsync(MachineSpec machine, CancellationToken cancellationToken)
    {
        var rid = await DetectRidAsync(machine, cancellationToken).ConfigureAwait(false);
        if (rid.StartsWith("win-", StringComparison.Ordinal))
            throw new NotSupportedException(WindowsUnsupported);
        var values = KeyValues(await SshHostLauncher.RunRemoteAsync(machine, ProbeScript(machine, rid), cancellationToken).ConfigureAwait(false));
        return new Installation(rid, values.GetValueOrDefault("home") ?? "", NonEmpty(values, "path"), NonEmpty(values, "version"));
    }

    /** 远端平台 → .NET RID(可按需扩展; 目前覆盖 glibc Linux / macOS / Windows)。 */
    public static string? RidFor(string os, string arch) => (os.Trim().ToLowerInvariant(), arch.Trim().ToLowerInvariant()) switch
    {
        ("linux", "x86_64") or ("linux", "amd64") => "linux-x64",
        ("linux", "aarch64") or ("linux", "arm64") => "linux-arm64",
        ("darwin", "x86_64") => "osx-x64",
        ("darwin", "arm64") or ("darwin", "aarch64") => "osx-arm64",
        ("windows", "x86_64") or ("windows", "amd64") => "win-x64",
        ("windows", "aarch64") or ("windows", "arm64") => "win-arm64",
        _ => null,
    };

    public static string AssetName(string rid) => $"dsharp-{rid}.tar.gz";

    /** Release 资产 URL: latest 或指定版本(自动补 v 前缀)。 */
    public static string AssetUrl(DeploySettings settings, string rid)
    {
        var asset = AssetName(rid);
        var baseUrl = settings.ReleaseBaseUrl.TrimEnd('/');
        if (string.IsNullOrEmpty(settings.ReleaseVersion) || settings.ReleaseVersion == DeploySettings.LatestVersion)
            return $"{baseUrl}/latest/download/{asset}";
        var tag = settings.ReleaseVersion.StartsWith('v') ? settings.ReleaseVersion : $"v{settings.ReleaseVersion}";
        return $"{baseUrl}/download/{tag}/{asset}";
    }

    /**
     * 远端直接下载并解压的脚本: 用远端自带代理(curl/wget 认 http_proxy/https_proxy 环境)或 ServerProxy; 无 curl/wget 时非零退出以触发 auto 回退。
     * 下载成功后才停旧宿主(stopCommand), 再解压覆盖: 下载失败不影响正在运行的宿主, 解压时旧进程已不再使用这些文件。
     */
    public static string BuildServerDownloadScript(DeploySettings settings, string rid, string stopCommand)
    {
        var url = AssetUrl(settings, rid);
        var proxy = settings.ServerProxy?.Trim() ?? "";
        // 代理地址在远端解析: curl 用 -x, wget 用 use_proxy + http(S)_proxy。
        var curlProxy = proxy.Length == 0 ? "" : $"-x '{proxy}' ";
        var wgetProxy = proxy.Length == 0 ? "" : $"-e use_proxy=yes -e https_proxy='{proxy}' -e http_proxy='{proxy}' ";
        var dir = InstallDirectory(rid);
        return "set -e; "
            + $"mkdir -p {dir}; "
            + $"cd {dir}; "
            + $"if command -v curl >/dev/null 2>&1; then curl -fL --connect-timeout 15 --max-time 900 --retry 2 {curlProxy}-o dsharp.tar.gz '{url}'; "
            + $"elif command -v wget >/dev/null 2>&1; then wget {wgetProxy}--timeout=15 --tries=2 -O dsharp.tar.gz '{url}'; "
            + $"else echo __NO_DOWNLOADER__ >&2; exit {NoDownloaderExitCode}; fi; "
            + stopCommand
            + "tar -xf dsharp.tar.gz; "
            + "chmod +x dsharp; "
            + "rm -f dsharp.tar.gz; "
            + "pwd";
    }

    /**
     * 停掉由 installedPath 这个二进制跑着的宿主(所有 harness home), 并等它们退出, 让新部署在下一次连接时生效。
     * `host --serve --stdio` 只把 stdio 桥接到已有的宿主, 不停的话新二进制永远不会被加载。
     * 只匹配该路径的 `host serve`, 不碰同一台机器上别的 dsharp; 外部服务的停启由用户自行管理。
     * 模式里的 `[h]ost` 让 pkill/pgrep 不匹配到执行脚本的 shell 自己(它的命令行里只有字面量 "[h]ost")。
     */
    public static string StopHostCommand(string? installedPath)
    {
        if (string.IsNullOrEmpty(installedPath))
            return "";
        var pattern = RemoteShell.Quote($"{Regex.Escape(installedPath)} [h]ost serve");
        return $"pkill -f {pattern} >/dev/null 2>&1 || true; "
            + $"i=0; while pgrep -f {pattern} >/dev/null 2>&1 && [ $i -lt {StopWaitAttempts} ]; do sleep 0.2; i=$((i+1)); done; ";
    }

    private static string InstallDirectory(string rid) => $"\"$HOME/.dsharp/{rid}\"";

    /** 探测脚本: 依次找本工具的部署目录、PATH、~/.local/bin; 输出 home/path/version 三个键(找不到 dsharp 时只有 home)。 */
    private static string ProbeScript(MachineSpec machine, string rid)
        => RemoteShell.Prelude(machine)
            + RemoteShell.ResolveHome
            + "p=\"\"; "
            + $"if [ -x \"$HOME/.dsharp/{rid}/dsharp\" ]; then p=\"$HOME/.dsharp/{rid}/dsharp\"; "
            + "elif command -v dsharp >/dev/null 2>&1; then p=$(command -v dsharp); "
            + "elif [ -x \"$HOME/.local/bin/dsharp\" ]; then p=\"$HOME/.local/bin/dsharp\"; fi; "
            + "echo \"home=$h\"; "
            + $"if [ -n \"$p\" ]; then echo \"path=$p\"; echo \"version=$(cat \"$(dirname \"$p\")/{VersionFileName}\" 2>/dev/null)\"; fi";

    private static async Task<string> DeployAsync(
        MachineSpec machine,
        DeploySettings settings,
        string rid,
        string stop,
        Action<string>? progress,
        Func<DeploySettings, string, CancellationToken, Task<string>>? archiveFactory,
        OutboundProxy? localProxy,
        CancellationToken cancellationToken)
    {
        // server/auto: 先让远端用它自己的网络/自带代理下载; auto 失败(无下载器/无外网/404)再回退到"本地下载 + scp 上传"。
        if (settings.Via is DownloadVia.Server or DownloadVia.Auto)
        {
            try
            {
                progress?.Invoke($"正在让远端直接下载 {AssetName(rid)} …");
                return await DeployFromServerAsync(machine, settings, rid, stop, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (settings.Via == DownloadVia.Auto && error is not OperationCanceledException)
            {
                progress?.Invoke($"远端直接下载不可用({error.Message}), 改由本地下发 …");
            }
        }

        progress?.Invoke($"正在准备 {AssetName(rid)} …");
        var archive = archiveFactory is null
            ? await DownloadAsync(settings, rid, localProxy ?? new OutboundProxy(), cancellationToken).ConfigureAwait(false)
            : await archiveFactory(settings, rid, cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Invoke($"正在上传并部署 dsharp({rid})到 ~/.dsharp/ …");
            return await UploadAndDeployAsync(machine, archive, rid, stop, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (archiveFactory is null)
                TryDelete(archive);
        }
    }

    /** 部署成功后写入版本标记: 它是「远端部署是否已过期」的唯一判据。 */
    private static Task<string> WriteIdentityAsync(MachineSpec machine, string rid, string identity, CancellationToken cancellationToken)
        => SshHostLauncher.RunRemoteAsync(
            machine,
            $"printf '%s' '{identity.Replace("'", "", StringComparison.Ordinal)}' > \"$HOME/.dsharp/{rid}/{VersionFileName}\"",
            cancellationToken);

    /** 远端平台识别: 先 `uname`(POSIX), 失败再试 PowerShell / cmd(Windows 远端默认 shell 多为 cmd)。 */
    private static async Task<string> DetectRidAsync(MachineSpec machine, CancellationToken cancellationToken)
    {
        if (await TryRunAsync(machine, "uname -s; uname -m", cancellationToken).ConfigureAwait(false) is { } uname)
        {
            var parts = Lines(uname);
            if (parts.Length >= 2 && RidFor(parts[0], parts[1]) is { } rid)
                return rid;
        }

        if (await TryRunAsync(machine, "powershell -NoProfile -NonInteractive -Command \"$env:PROCESSOR_ARCHITECTURE\"", cancellationToken).ConfigureAwait(false) is { } powershell
            && RidFor("windows", LastLine(powershell)) is { } psRid)
        {
            return psRid;
        }

        if (await TryRunAsync(machine, "cmd /c echo %PROCESSOR_ARCHITECTURE%", cancellationToken).ConfigureAwait(false) is { } cmd
            && RidFor("windows", LastLine(cmd)) is { } cmdRid)
        {
            return cmdRid;
        }

        throw new NotSupportedException("无法识别远端平台(既非 POSIX uname, 也非 Windows)");
    }

    /** 探测命令: 成功返回 stdout; 远端命令失败返回 null(继续试下一种平台探测); ssh 自身失败必须抛出, 不能伪装成“平台未知”。 */
    private static async Task<string?> TryRunAsync(MachineSpec machine, string command, CancellationToken cancellationToken)
    {
        var result = await SshHostLauncher.RunRemoteCaptureAsync(machine, command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == SshFailureExitCode)
            throw new IOException($"ssh {machine.User}@{machine.Host} 失败: {result.FailureDetail}");
        return result.ExitCode == 0 ? result.StdOut : null;
    }

    /** `key=value` 行 → 字典(按第一个 '=' 切分, 后出现的同名键覆盖前者)。 */
    internal static Dictionary<string, string> KeyValues(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var cut = line.IndexOf('=');
            if (cut > 0)
                values[line[..cut]] = line[(cut + 1)..];
        }

        return values;
    }

    private static string? NonEmpty(Dictionary<string, string> values, string key)
        => values.GetValueOrDefault(key) is { Length: > 0 } value ? value : null;

    private static string[] Lines(string output)
        => output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string LastLine(string output) => Lines(output).LastOrDefault() ?? "";

    private static async Task<string> DownloadAsync(DeploySettings settings, string rid, OutboundProxy proxy, CancellationToken cancellationToken)
    {
        var url = AssetUrl(settings, rid);
        using var client = new HttpClient(proxy.CreateHandler()) { Timeout = TimeSpan.FromMinutes(10) };
        var bytes = await client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(Path.GetTempPath(), $"dsharp-{rid}-{Guid.NewGuid():N}.tar.gz");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static async Task<string> UploadAndDeployAsync(MachineSpec machine, string archive, string rid, string stop, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var archiveName = $".dsharp-upload-{token}.tar.gz";
        await SshHostLauncher.UploadAsync(machine, archive, archiveName, cancellationToken).ConfigureAwait(false);

        // 远端先停旧宿主, 再解压到 ~/.dsharp/<rid>/(归档根即发布输出), 打印解压后 dsharp 的目录。tar -xf 自动识别 gzip。
        var dir = InstallDirectory(rid);
        var script =
            "set -e; "
            + $"mkdir -p {dir}; "
            + stop
            + $"tar -xf \"$HOME/{archiveName}\" -C {dir}; "
            + $"chmod +x {dir}/dsharp; "
            + $"rm -f \"$HOME/{archiveName}\"; "
            + $"cd {dir} && pwd";
        return CommandIn(await SshHostLauncher.RunRemoteAsync(machine, script, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> DeployFromServerAsync(MachineSpec machine, DeploySettings settings, string rid, string stop, CancellationToken cancellationToken)
        => CommandIn(await SshHostLauncher.RunRemoteAsync(machine, BuildServerDownloadScript(settings, rid, stop), cancellationToken).ConfigureAwait(false));

    private static string CommandIn(string printedDirectory)
    {
        var dir = printedDirectory.Trim();
        return dir.Length == 0
            ? throw new IOException("远端部署完成但未返回安装目录")
            : $"{dir.TrimEnd('/')}/dsharp";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
