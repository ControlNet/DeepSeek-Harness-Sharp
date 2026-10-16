using System.Diagnostics;
using System.Runtime.InteropServices;
using Dsh.Boot;
using OpenTK.Graphics;
using OpenTK.Graphics.Vulkan;
// 裸名 `Vk` 会被外层命名空间 Dsh.Tui.Vk 遮蔽, 用编译单元别名指向静态绑定类。
using VulkanApi = OpenTK.Graphics.Vulkan.Vk;

namespace Dsh.Tui.Vk;

/**
 * IVkRepairIo 的真实 Linux 实现: 文件/环境变量/子进程/dlopen/Xvfb。
 * 决策逻辑在本类之外(见 VkLinuxEnvironmentRepair); 本类只做事、不判断该不该做。
 * 只在 Linux 上被构造(见 VkLinuxEnvironmentBootstrap), 因此这里的 P/Invoke 依赖 libc / libdl。
 */
internal sealed class VkRepairRealIo : IVkRepairIo
{
    private const int RtldLazy = 0x1;
    private const int RtldNow = 0x2;
    private const int RtldGlobal = 0x100;
    private const uint RootUid = 0;
    private const int CommandTimeoutMilliseconds = 120_000;

    private static readonly string[] CompilerCandidates = ["cc", "gcc", "clang"];

    /** Vulkan loader 的 soname: 装了 libvulkan1 就有第一个。 */
    private static readonly string[] VulkanLoaderNames = ["libvulkan.so.1", "libvulkan.so"];

    private static readonly string[] NvidiaManifestPaths =
    [
        "/usr/share/vulkan/icd.d/nvidia_icd.json",
        "/etc/vulkan/icd.d/nvidia_icd.json",
        "/usr/local/share/vulkan/icd.d/nvidia_icd.json",
    ];

    private static readonly string[] LibraryDirectories =
    [
        "/usr/lib/x86_64-linux-gnu",
        "/usr/lib/aarch64-linux-gnu",
        "/usr/lib64",
        "/usr/lib",
    ];

    public bool IsLinux => OperatingSystem.IsLinux();

    public bool IsRoot
    {
        get
        {
            try
            {
                return GetEuid() == RootUid;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public string CacheDirectory => Path.Combine(HarnessHome.Resolve().CachePath, "vk-accel");

    /** dlopen 探测 soname(RTLD_LAZY): 能打开就说明 loader 在可加载路径上。 */
    public bool VulkanLoaderPresent
    {
        get
        {
            if (!IsLinux)
                return true;
            foreach (var name in VulkanLoaderNames)
            {
                var handle = Dlopen(name, RtldLazy);
                if (handle == IntPtr.Zero)
                    continue;
                _ = Dlclose(handle);
                return true;
            }

            return false;
        }
    }

    /** 只装 loader; 驱动/ICD 由发行版或厂商包单独提供。无法识别的发行版返回 null。 */
    public VkInstallCommand? VulkanLoaderInstallCommand
    {
        get
        {
            if (!IsLinux)
                return null;
            if (FindOnPath("apt-get") is not null)
                return new VkInstallCommand("apt-get", "install -y -qq libvulkan1");
            if (FindOnPath("dnf") is not null)
                return new VkInstallCommand("dnf", "install -y vulkan-loader");
            if (FindOnPath("zypper") is not null)
                return new VkInstallCommand("zypper", "--non-interactive install -y libvulkan1");
            if (FindOnPath("pacman") is not null)
                return new VkInstallCommand("pacman", "-S --noconfirm --needed vulkan-icd-loader");
            if (FindOnPath("apk") is not null)
                return new VkInstallCommand("apk", "add --no-cache vulkan-loader");
            return null;
        }
    }

    /** sudo -n(免密)可用与否; 只探测一次, 避免反复起进程。 */
    public bool SudoAvailable
    {
        get
        {
            if (!IsLinux || FindOnPath("sudo") is null)
                return false;
            return Run("sudo", "-n true", null).ExitCode == 0;
        }
    }

    /**
     * 无 root / 无 sudo 时的用户态安装(apt/dpkg): apt-get download 取包(不需要权限) → dpkg-deb -x 解到缓存
     * → dlopen(RTLD_GLOBAL) 预载。之后 libvulkan.so.1 按 soname 命中已加载对象, 因此不必(也不能)靠
     * LD_LIBRARY_PATH —— glibc 在进程启动时就把搜索路径固定住了, 之后再 setenv 对 dlopen 无效。
     * 其它发行版返回可读原因, 由调用方提示手动安装。
     */
    public string? InstallVulkanLoaderUserSpace()
    {
        var downloader = FindOnPath("apt-get");
        var extractor = FindOnPath("dpkg-deb");
        if (downloader is null || extractor is null)
            return "未找到 apt-get/dpkg-deb, 无法用户态安装(请手动安装 libvulkan1)";

        var downloadDirectory = Path.Combine(CacheDirectory, "download");
        var libraryDirectory = Path.Combine(CacheDirectory, "lib");
        try
        {
            Directory.CreateDirectory(downloadDirectory);
            Directory.CreateDirectory(libraryDirectory);
            foreach (var stale in Directory.EnumerateFiles(downloadDirectory, "libvulkan*.deb"))
                File.Delete(stale);
        }
        catch (Exception error)
        {
            return $"准备用户态安装目录失败: {error.Message}";
        }

        var download = Run(downloader, "download libvulkan1", downloadDirectory);
        var package = Directory.EnumerateFiles(downloadDirectory, "libvulkan1*.deb").FirstOrDefault();
        if (download.ExitCode != 0 || package is null)
            return $"apt-get download libvulkan1 失败(exit={download.ExitCode}): {Tail(download.Output)}";

        var extract = Run(extractor, $"-x \"{package}\" \"{libraryDirectory}\"", null);
        if (extract.ExitCode != 0)
            return $"dpkg-deb -x 失败(exit={extract.ExitCode}): {Tail(extract.Output)}";

        var library = FindLoaderIn(libraryDirectory);
        if (library is null)
            return "解包后未找到 libvulkan.so.1";
        var load = TryLoadGlobalLibrary(library);
        return load.Loaded ? null : $"预载解包出的 loader 失败: {load.Error}";
    }

    public bool NvidiaIcdPresent
    {
        get
        {
            if (!IsLinux)
                return false;
            foreach (var manifest in NvidiaManifestPaths)
            {
                if (File.Exists(manifest))
                    return true;
            }

            foreach (var directory in LibraryDirectories)
            {
                if (ContainsNvidiaGraphicsLibrary(directory))
                    return true;
            }

            return false;
        }
    }

    public string? GetEnv(string name) => Environment.GetEnvironmentVariable(name);

    public void SetEnv(string name, string value)
    {
        if (TrySetEnvNative(name, value))
            return;
        Environment.SetEnvironmentVariable(name, value);
    }

    public bool FileExists(string path) => File.Exists(path);

    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);

    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public string? FindCompiler()
    {
        foreach (var candidate in CompilerCandidates)
        {
            if (FindOnPath(candidate) is { } path)
                return path;
        }

        return null;
    }

    public string? FindXvfb() => FindOnPath("Xvfb");

    public VkProbeResult Probe() => VkHardwareProbe.Run();

    public VkCommandResult Run(string fileName, string arguments, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            startInfo.WorkingDirectory = workingDirectory;

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new VkCommandResult(-1, $"无法启动 {fileName}");
            process.StandardInput.Close();
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(CommandTimeoutMilliseconds))
            {
                _ = TryKill(process);
                return new VkCommandResult(-1, "命令超时");
            }

            return new VkCommandResult(process.ExitCode, standardOutput + standardError);
        }
        catch (Exception error)
        {
            return new VkCommandResult(-1, error.Message);
        }
    }

    /** RTLD_NOW | RTLD_GLOBAL: 必须让 shim 的符号进入全局作用域, 才能被随后 dlopen 的 ICD 解析到。 */
    public VkLibraryLoad TryLoadGlobalLibrary(string path)
    {
        try
        {
            var handle = Dlopen(path, RtldNow | RtldGlobal);
            return handle != IntPtr.Zero ? new VkLibraryLoad(true, "") : new VkLibraryLoad(false, DlopenError());
        }
        catch (Exception error)
        {
            return new VkLibraryLoad(false, error.Message);
        }
    }

    public VkVirtualDisplayStart StartVirtualDisplay(int width, int height)
    {
        var xvfb = FindOnPath("Xvfb");
        if (xvfb is null)
            return new VkVirtualDisplayStart(null, "未找到 Xvfb 可执行文件");

        var display = FindFreeDisplayNumber();
        var startInfo = new ProcessStartInfo(xvfb)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add($":{display}");
        startInfo.ArgumentList.Add("-screen");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add($"{width}x{height}x24");
        startInfo.ArgumentList.Add("-nolisten");
        startInfo.ArgumentList.Add("tcp");

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
                return new VkVirtualDisplayStart(null, "无法启动 Xvfb 进程");
            process.StandardInput.Close();
            return new VkVirtualDisplayStart(new VkXvfbDisplay(process, display), "");
        }
        catch (Exception error)
        {
            return new VkVirtualDisplayStart(null, error.Message);
        }
    }

    public bool VirtualDisplayReady(int displayNumber) => File.Exists($"/tmp/.X11-unix/X{displayNumber}");

    public void Log(string message)
    {
        var line = $"[vk-accel] {message}";
        _ = TryWriteErrorLine(line);
        _ = TryAppendLogFile(line);
    }

    private static bool TryWriteErrorLine(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private bool TryAppendLogFile(string line)
    {
        try
        {
            var directory = CacheDirectory;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "repair.log"), $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ContainsNvidiaGraphicsLibrary(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "libGLX_nvidia.so*").Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var directory in path.Split(':'))
        {
            if (directory.Length == 0)
                continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static int FindFreeDisplayNumber()
    {
        for (var candidate = 99; candidate >= 1; candidate--)
        {
            if (!File.Exists($"/tmp/.X11-unix/X{candidate}") && !File.Exists($"/tmp/.X{candidate}-lock"))
                return candidate;
        }

        return 99;
    }

    private static string? FindLoaderIn(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "libvulkan.so.1*", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[^400..];
    }

    private static string DlopenError()
    {
        var pointer = Dlerror();
        return pointer == IntPtr.Zero ? "dlopen 返回 NULL" : Marshal.PtrToStringAnsi(pointer) ?? "dlopen 失败";
    }

    private static bool TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TrySetEnvNative(string name, string value)
    {
        try
        {
            return SetEnvNative(name, value, 1) == 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "setenv", CharSet = CharSet.Ansi)]
    private static extern int SetEnvNative(string name, string value, int overwrite);

    [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr Dlopen(string fileName, int flags);

    [DllImport("libdl.so.2", EntryPoint = "dlclose")]
    private static extern int Dlclose(IntPtr handle);

    [DllImport("libdl.so.2", EntryPoint = "dlerror")]
    private static extern IntPtr Dlerror();

    private sealed class VkXvfbDisplay(Process process, int displayNumber) : IVirtualDisplay
    {
        public int DisplayNumber { get; } = displayNumber;

        public void Dispose()
        {
            _ = TryKill(process);
            process.Dispose();
        }
    }
}

/** 轻量探测: 建一次实例 + 枚举物理设备, 判断是否已有非 CPU/Other 的硬件设备。不做设备/队列创建。 */
internal static unsafe class VkHardwareProbe
{
    public static VkProbeResult Run()
    {
        try
        {
            VkGpuDevice.EnsureInitialized();
            var appInfo = new VkApplicationInfo
            {
                sType = VkStructureType.StructureTypeApplicationInfo,
                apiVersion = VulkanApi.MAKE_VERSION(1, 1, 0),
            };
            var createInfo = new VkInstanceCreateInfo
            {
                sType = VkStructureType.StructureTypeInstanceCreateInfo,
                pApplicationInfo = &appInfo,
            };
            VkInstance instance;
            var created = VulkanApi.CreateInstance(&createInfo, null, &instance);
            if (created != VkResult.Success)
                return new VkProbeResult(false, 0, false, "", $"vkCreateInstance: {created}");

            try
            {
                VKLoader.SetInstance(instance);
                return Enumerate(instance);
            }
            finally
            {
                VulkanApi.DestroyInstance(instance, null);
                VKLoader.SetInstance(default);
            }
        }
        catch (Exception error)
        {
            return new VkProbeResult(false, 0, false, "", $"{error.GetType().Name}: {error.Message}");
        }
    }

    private static VkProbeResult Enumerate(VkInstance instance)
    {
        uint count = 0;
        var query = VulkanApi.EnumeratePhysicalDevices(instance, &count, null);
        if (query != VkResult.Success)
            return new VkProbeResult(true, 0, false, "", $"vkEnumeratePhysicalDevices: {query}");
        if (count == 0)
            return new VkProbeResult(true, 0, false, "", null);

        var handles = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* pointer = handles)
        {
            var fill = VulkanApi.EnumeratePhysicalDevices(instance, &count, pointer);
            if (fill != VkResult.Success)
                return new VkProbeResult(true, 0, false, "", $"vkEnumeratePhysicalDevices: {fill}");
        }

        var names = new List<string>((int)count);
        var hardware = false;
        foreach (var handle in handles)
        {
            VkPhysicalDeviceProperties properties;
            VulkanApi.GetPhysicalDeviceProperties(handle, &properties);
            var name = DecodeName(ref properties.deviceName[0]);
            names.Add($"{name}[{properties.deviceType}]");
            if (properties.deviceType is not (VkPhysicalDeviceType.PhysicalDeviceTypeCpu or VkPhysicalDeviceType.PhysicalDeviceTypeOther))
                hardware = true;
        }

        return new VkProbeResult(true, (int)count, hardware, string.Join(", ", names), null);
    }

    private static string DecodeName(ref byte first)
    {
        var span = MemoryMarshal.CreateReadOnlySpan(ref first, 256);
        var end = span.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }
}
