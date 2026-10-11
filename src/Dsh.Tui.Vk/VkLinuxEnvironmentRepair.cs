namespace Dsh.Tui.Vk;

internal enum VkRepairStatus
{
    NotApplicable,
    Disabled,
    AlreadyUsable,
    Repaired,
    Failed,
}

/** 自愈结果: Detail 说明做了什么(成功)或哪一步失败、原始错误(失败), 绝不含糊成功。 */
internal readonly record struct VkRepairOutcome(VkRepairStatus Status, string Detail)
{
    public bool HardwareUsable => Status is VkRepairStatus.AlreadyUsable or VkRepairStatus.Repaired;
}

/** 轻量探测结果: 是否建出实例、发现几块设备、是否含非 CPU/Other 的硬件设备。 */
internal readonly record struct VkProbeResult(bool InstanceCreated, int DeviceCount, bool HardwareDevicePresent, string Devices, string? Error);

internal readonly record struct VkCommandResult(int ExitCode, string Output);

internal readonly record struct VkLibraryLoad(bool Loaded, string Error);

internal readonly record struct VkVirtualDisplayStart(IVirtualDisplay? Display, string Error);

internal interface IVirtualDisplay : IDisposable
{
    int DisplayNumber { get; }
}

/**
 * 自愈所需的全部 IO 缝隙: 探测 / 文件 / 进程 / 环境变量 / dlopen / 虚拟 X。
 * 决策逻辑只依赖这些方法, 因此可在 Windows 上用假实现覆盖"何时该做什么/失败原因", 不依赖真实 GPU/容器。
 */
internal interface IVkRepairIo
{
    bool IsLinux { get; }

    bool IsRoot { get; }

    /** 缓存根目录: DSH_HOME/cache/vk-accel。 */
    string CacheDirectory { get; }

    bool NvidiaIcdPresent { get; }

    string? GetEnv(string name);

    void SetEnv(string name, string value);

    bool FileExists(string path);

    void WriteAllText(string path, string content);

    void EnsureDirectory(string path);

    string? FindCompiler();

    string? FindXvfb();

    VkProbeResult Probe();

    VkCommandResult Run(string fileName, string arguments, string? workingDirectory);

    VkLibraryLoad TryLoadGlobalLibrary(string path);

    VkVirtualDisplayStart StartVirtualDisplay(int width, int height);

    bool VirtualDisplayReady(int displayNumber);

    void Log(string message);
}

/**
 * Vulkan 环境自愈编排(Linux 专属, best-effort)。
 * 流程: 轻量探测 → 可用即 no-op; 否则在存在 NVIDIA ICD 时先补符号 shim, 仍失败再给一条虚拟 X;
 * 每步之后重探测, 成功记录做了什么, 失败返回可读原因, 且绝不谎报成功、绝不把进程带崩。
 */
internal sealed class VkLinuxEnvironmentRepair(IVkRepairIo io, bool enabled) : IDisposable
{
    private const int VirtualDisplayWidth = 1280;
    private const int VirtualDisplayHeight = 720;
    private const int DisplayPollMilliseconds = 50;
    private const long DisplayReadyTimeoutMilliseconds = 5000;

    private IVirtualDisplay? _virtualDisplay;

    public VkRepairOutcome EnsureReady()
    {
        var outcome = Run();
        if (outcome.Status != VkRepairStatus.AlreadyUsable)
            io.Log($"{outcome.Status}: {outcome.Detail}");
        return outcome;
    }

    public void Dispose()
    {
        _virtualDisplay?.Dispose();
        _virtualDisplay = null;
    }

    private VkRepairOutcome Run()
    {
        if (!io.IsLinux)
            return new VkRepairOutcome(VkRepairStatus.NotApplicable, "非 Linux 平台, 环境自愈不适用");
        if (!enabled)
            return new VkRepairOutcome(VkRepairStatus.Disabled, "环境自愈已禁用");

        var first = SafeProbe();
        if (first.HardwareDevicePresent)
            return new VkRepairOutcome(VkRepairStatus.AlreadyUsable, $"硬件 Vulkan 已可用: {first.Devices}");
        if (!io.NvidiaIcdPresent)
            return new VkRepairOutcome(VkRepairStatus.Failed, $"无硬件 Vulkan 设备且未检测到 NVIDIA ICD: {Describe(first)}");

        var actions = new List<string>();
        var shimFailure = TryEnsureShim(actions);
        var afterShim = SafeProbe();
        if (afterShim.HardwareDevicePresent)
            return Repaired(actions, afterShim);

        var displayFailure = TryStartVirtualX(actions);
        var afterDisplay = SafeProbe();
        if (afterDisplay.HardwareDevicePresent)
            return Repaired(actions, afterDisplay);

        return new VkRepairOutcome(VkRepairStatus.Failed, BuildFailure(shimFailure, displayFailure, first, afterDisplay));
    }

    private static VkRepairOutcome Repaired(List<string> actions, VkProbeResult probe)
    {
        var steps = actions.Count == 0 ? "无需额外步骤" : string.Join("; ", actions);
        return new VkRepairOutcome(VkRepairStatus.Repaired, $"{steps}; 硬件设备: {probe.Devices}");
    }

    /** 步骤 1: 写 shim.c(内嵌源码) → 复用或编译 shim.so → 以 RTLD_GLOBAL 载入全局符号表(必须先于 ICD 的 dlopen)。 */
    private string? TryEnsureShim(List<string> actions)
    {
        try
        {
            var cacheDirectory = io.CacheDirectory;
            io.EnsureDirectory(cacheDirectory);
            var sourcePath = Path.Combine(cacheDirectory, VkRepairShim.SourceFileName);
            io.WriteAllText(sourcePath, VkRepairShim.Source);
            var libraryPath = Path.Combine(cacheDirectory, VkRepairShim.LibraryFileName);
            if (!io.FileExists(libraryPath))
            {
                var buildFailure = BuildShim(cacheDirectory, sourcePath, libraryPath);
                if (buildFailure is not null)
                    return buildFailure;
            }

            var load = io.TryLoadGlobalLibrary(libraryPath);
            if (!load.Loaded)
                return $"dlopen 符号 shim 失败: {load.Error}";
            actions.Add($"加载符号 shim({Path.GetFileName(libraryPath)})");
            return null;
        }
        catch (Exception error)
        {
            return $"符号 shim 步骤异常: {error.Message}";
        }
    }

    private string? BuildShim(string cacheDirectory, string sourcePath, string libraryPath)
    {
        var compiler = io.FindCompiler();
        if (compiler is null && io.IsRoot)
        {
            io.Log("未找到 C 编译器, 以 root 尝试 apt-get install gcc");
            io.Run("apt-get", "install -y -qq gcc", null);
            compiler = io.FindCompiler();
        }

        if (compiler is null)
            return "未找到 C 编译器(cc/gcc/clang), 且非 root 无法自动安装 gcc";

        var arguments = $"-shared -fPIC -O2 -w -o \"{libraryPath}\" \"{sourcePath}\"";
        var result = io.Run(compiler, arguments, cacheDirectory);
        return result.ExitCode == 0 ? null : $"编译符号 shim 失败(exit={result.ExitCode}): {Tail(result.Output)}";
    }

    /** 步骤 2: 仅在仍失败、无用户显示、且系统有 NVIDIA ICD 时, 起一个 Xvfb 并把 DISPLAY 指过去。 */
    private string? TryStartVirtualX(List<string> actions)
    {
        if (HasUserDisplay())
            return "已存在 DISPLAY/WAYLAND_DISPLAY, 不接管用户显示服务器";

        var availability = EnsureXvfb();
        if (availability is not null)
            return availability;

        try
        {
            var start = io.StartVirtualDisplay(VirtualDisplayWidth, VirtualDisplayHeight);
            if (start.Display is null)
                return $"启动 Xvfb 失败: {start.Error}";
            _virtualDisplay = start.Display;
            var display = start.Display.DisplayNumber;
            if (!WaitDisplayReady(display))
                return $"Xvfb 已启动但 socket 未就绪(:{display})";
            io.SetEnv("DISPLAY", $":{display}");
            actions.Add($"启动虚拟 X(Xvfb :{display})");
            return null;
        }
        catch (Exception error)
        {
            return $"虚拟 X 步骤异常: {error.Message}";
        }
    }

    private string? EnsureXvfb()
    {
        if (io.FindXvfb() is not null)
            return null;
        if (!io.IsRoot)
            return "系统未安装 Xvfb, 且非 root 无法自动安装";
        io.Log("未找到 Xvfb, 以 root 尝试 apt-get install xvfb");
        io.Run("apt-get", "install -y -qq xvfb", null);
        return io.FindXvfb() is null ? "apt-get install xvfb 后仍未找到 Xvfb" : null;
    }

    private bool HasUserDisplay()
        => !string.IsNullOrEmpty(io.GetEnv("DISPLAY")) || !string.IsNullOrEmpty(io.GetEnv("WAYLAND_DISPLAY"));

    private bool WaitDisplayReady(int display)
    {
        var deadline = Environment.TickCount64 + DisplayReadyTimeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (io.VirtualDisplayReady(display))
                return true;
            Thread.Sleep(DisplayPollMilliseconds);
        }

        return io.VirtualDisplayReady(display);
    }

    private VkProbeResult SafeProbe()
    {
        try
        {
            return io.Probe();
        }
        catch (Exception error)
        {
            return new VkProbeResult(false, 0, false, "", $"探测异常: {error.Message}");
        }
    }

    private static string BuildFailure(string? shimFailure, string? displayFailure, VkProbeResult first, VkProbeResult last)
    {
        var parts = new List<string>();
        if (shimFailure is not null)
            parts.Add($"符号 shim: {shimFailure}");
        if (displayFailure is not null)
            parts.Add($"虚拟 X: {displayFailure}");
        parts.Add($"初始探测: {Describe(first)}");
        parts.Add($"最终探测: {Describe(last)}");
        return string.Join(" | ", parts);
    }

    private static string Describe(VkProbeResult probe)
        => probe.Error is { Length: > 0 } error
            ? error
            : probe.DeviceCount == 0
                ? "未发现物理设备"
                : $"仅有非硬件设备: {probe.Devices}";

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[^400..];
    }
}
