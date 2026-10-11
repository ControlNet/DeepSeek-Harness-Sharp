using Dsh.Tui.Vk;

namespace Dsh.Tests;

/**
 * Vulkan 环境自愈(VkLinuxEnvironmentRepair)的决策用例。
 * 通过注入假 IVkRepairIo 覆盖"何时该做什么 / 失败原因", 不依赖真实 GPU / 容器, 在 Windows 上即可跑。
 */
public sealed class VkLinuxEnvironmentRepairTests
{
    [Fact]
    public void NonLinux_IsNoOp()
    {
        var io = new FakeRepairIo { IsLinux = false };
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.NotApplicable, outcome.Status);
        Assert.Equal(0, io.ProbeCount);
        Assert.Empty(io.Commands);
        Assert.Empty(io.WrittenFiles);
        Assert.Empty(io.Dlopens);
    }

    [Fact]
    public void HardwareAlreadyUsable_DoesNothing()
    {
        var io = new FakeRepairIo { Probes = [Hardware()] };
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.AlreadyUsable, outcome.Status);
        Assert.True(outcome.HardwareUsable);
        Assert.Equal(1, io.ProbeCount);
        Assert.Empty(io.Commands);
        Assert.Empty(io.WrittenFiles);
        Assert.Empty(io.Dlopens);
        Assert.Equal(0, io.StartedDisplays);
    }

    [Fact]
    public void Disabled_SkipsProbe()
    {
        var io = new FakeRepairIo { Probes = [NoHardware()] };
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: false).EnsureReady();

        Assert.Equal(VkRepairStatus.Disabled, outcome.Status);
        Assert.Equal(0, io.ProbeCount);
    }

    [Fact]
    public void NoNvidiaIcd_FailsWithReason()
    {
        var io = new FakeRepairIo { Probes = [NoHardware()], NvidiaIcdPresent = false };
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.Failed, outcome.Status);
        Assert.Contains("NVIDIA", outcome.Detail);
        Assert.Empty(io.Commands);
        Assert.Empty(io.Dlopens);
        Assert.Equal(0, io.StartedDisplays);
    }

    [Fact]
    public void ShimMakesHardwareUsable_RepairedWithoutX()
    {
        var io = new FakeRepairIo
        {
            Probes = [NoHardware(), Hardware()],
            NvidiaIcdPresent = true,
            Compiler = "cc",
        };
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.Repaired, outcome.Status);
        Assert.True(outcome.HardwareUsable);
        Assert.Contains(io.WrittenFiles, path => path.EndsWith("shim.c", StringComparison.Ordinal));
        var compile = Assert.Single(io.Commands);
        Assert.Equal("cc", compile.FileName);
        Assert.Single(io.Dlopens);
        Assert.Equal(0, io.StartedDisplays);
        Assert.False(io.Env.ContainsKey("DISPLAY"));
    }

    [Fact]
    public void CachedShim_IsNotRecompiled()
    {
        var cacheDirectory = "/home/u/.dsh/cache/vk-accel";
        var libraryPath = Path.Combine(cacheDirectory, VkRepairShim.LibraryFileName);
        var io = new FakeRepairIo
        {
            CacheDirectory = cacheDirectory,
            Probes = [NoHardware(), Hardware()],
            NvidiaIcdPresent = true,
            Compiler = "cc",
        };
        io.SeedFile(libraryPath);
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.Repaired, outcome.Status);
        Assert.Empty(io.Commands);
        var loaded = Assert.Single(io.Dlopens);
        Assert.Equal(libraryPath, loaded);
    }

    [Fact]
    public void NoCompilerNonRoot_FailsWithCompilerReason()
    {
        var io = new FakeRepairIo
        {
            Probes = [NoHardware(), NoHardware()],
            NvidiaIcdPresent = true,
            Compiler = null,
            IsRoot = false,
            Xvfb = null,
        };
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.Failed, outcome.Status);
        Assert.Contains("编译器", outcome.Detail);
        Assert.Empty(io.Commands);
        Assert.Empty(io.Dlopens);
        Assert.Equal(0, io.StartedDisplays);
    }

    [Fact]
    public void ShimLoadFails_FailsWithDlopenReason()
    {
        var io = new FakeRepairIo
        {
            Probes = [NoHardware(), NoHardware(), NoHardware()],
            NvidiaIcdPresent = true,
            Compiler = "cc",
            Xvfb = null,
            IsRoot = false,
        };
        io.LoadResult = new VkLibraryLoad(false, "undefined symbol: __malloc_hook");
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.Failed, outcome.Status);
        Assert.Contains("dlopen", outcome.Detail);
        Assert.Single(io.Dlopens);
        Assert.Equal(0, io.StartedDisplays);
    }

    [Fact]
    public void StillOnlyCpu_NoDisplay_StartsXvfbThenUsable()
    {
        var io = new FakeRepairIo
        {
            Probes = [NoHardware(), NoHardware(), Hardware()],
            NvidiaIcdPresent = true,
            Compiler = "cc",
            Xvfb = "/usr/bin/Xvfb",
        };
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.Repaired, outcome.Status);
        Assert.Equal(1, io.StartedDisplays);
        Assert.True(io.Env.TryGetValue("DISPLAY", out var display));
        Assert.StartsWith(":", display);
        Assert.Single(io.Dlopens);
    }

    [Fact]
    public void UserDisplayPresent_DoesNotStartXvfbAndDoesNotOverride()
    {
        var io = new FakeRepairIo
        {
            Probes = [NoHardware(), NoHardware()],
            NvidiaIcdPresent = true,
            Compiler = "cc",
            Xvfb = "/usr/bin/Xvfb",
        };
        io.Env["DISPLAY"] = ":0";
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.Failed, outcome.Status);
        Assert.Contains("DISPLAY", outcome.Detail);
        Assert.Equal(0, io.StartedDisplays);
        Assert.Equal(":0", io.Env["DISPLAY"]);
    }

    [Fact]
    public void XvfbMissingNonRoot_FailsWithXvfbReason()
    {
        var io = new FakeRepairIo
        {
            Probes = [NoHardware(), NoHardware(), NoHardware()],
            NvidiaIcdPresent = true,
            Compiler = "cc",
            Xvfb = null,
            IsRoot = false,
        };
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.Failed, outcome.Status);
        Assert.Contains("Xvfb", outcome.Detail);
        Assert.Equal(0, io.StartedDisplays);
    }

    [Fact]
    public void Root_InstallsCompilerAndXvfb_ThenRepairs()
    {
        var io = new FakeRepairIo
        {
            Probes = [NoHardware(), NoHardware(), Hardware()],
            NvidiaIcdPresent = true,
            Compiler = null,
            Xvfb = null,
            IsRoot = true,
        };
        io.RunEffect = (fileName, arguments) =>
        {
            if (fileName != "apt-get")
                return;
            if (arguments.Contains("gcc"))
                io.Compiler = "cc";
            if (arguments.Contains("xvfb"))
                io.Xvfb = "/usr/bin/Xvfb";
        };
        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.Repaired, outcome.Status);
        Assert.Contains(io.Commands, command => command.Arguments.Contains("install") && command.Arguments.Contains("gcc"));
        Assert.Contains(io.Commands, command => command.Arguments.Contains("install") && command.Arguments.Contains("xvfb"));
        Assert.Equal(1, io.StartedDisplays);
    }

    [Fact]
    public void IoThrows_IsCaughtAndReportedAsFailure()
    {
        var io = new FakeRepairIo
        {
            NvidiaIcdPresent = true,
            Compiler = null,
            Xvfb = null,
            IsRoot = false,
        };
        io.ProbeThrows = true;
        io.WriteThrows = true;

        var outcome = new VkLinuxEnvironmentRepair(io, enabled: true).EnsureReady();

        Assert.Equal(VkRepairStatus.Failed, outcome.Status);
        Assert.NotEmpty(io.Logs);
    }

    [Fact]
    public void ShimSource_ContainsAllSevenRequiredSymbols()
    {
        foreach (var symbol in new[]
                 {
                     "__malloc_hook", "__realloc_hook", "__free_hook", "__memalign_hook",
                     "ErrorF", "miCreateDefColormap", "xf86ProcessOptions",
                 })
        {
            Assert.Contains(symbol, VkRepairShim.Source);
        }
    }

    [Fact]
    public void ShimLibraryName_IsHashScoped()
    {
        Assert.Matches(@"^shim-[0-9a-f]{16}\.so$", VkRepairShim.LibraryFileName);
    }

    private static VkProbeResult NoHardware() => new(true, 1, false, "llvmpipe[Cpu]", null);

    private static VkProbeResult Hardware() => new(true, 1, true, "NVIDIA GeForce RTX 3080[DiscreteGpu]", null);

    private sealed class FakeVirtualDisplay(int displayNumber) : IVirtualDisplay
    {
        public int DisplayNumber { get; } = displayNumber;

        public void Dispose()
        {
        }
    }

    private sealed class FakeRepairIo : IVkRepairIo
    {
        public bool IsLinux { get; init; } = true;

        public bool IsRoot { get; set; }

        public string CacheDirectory { get; init; } = "/home/u/.dsh/cache/vk-accel";

        public bool NvidiaIcdPresent { get; init; }

        public string? Compiler { get; set; }

        public string? Xvfb { get; set; }

        public List<VkProbeResult> Probes { get; init; } = [];

        public VkLibraryLoad LoadResult { get; set; } = new(true, "");

        public Action<string, string>? RunEffect { get; set; }

        public bool ProbeThrows { get; set; }

        public bool WriteThrows { get; set; }

        public int ProbeCount { get; private set; }

        public int StartedDisplays { get; private set; }

        public Dictionary<string, string> Env { get; } = [];

        public List<(string FileName, string Arguments, string? WorkingDirectory)> Commands { get; } = [];

        public List<string> Dlopens { get; } = [];

        public List<string> WrittenFiles { get; } = [];

        public List<string> Logs { get; } = [];

        private readonly HashSet<string> _files = [];
        private int _probeIndex;

        public void SeedFile(string path) => _files.Add(path);

        public string? GetEnv(string name) => Env.TryGetValue(name, out var value) ? value : null;

        public void SetEnv(string name, string value) => Env[name] = value;

        public bool FileExists(string path) => _files.Contains(path);

        public void WriteAllText(string path, string content)
        {
            if (WriteThrows)
                throw new IOException("write failed");
            _files.Add(path);
            WrittenFiles.Add(path);
        }

        public void EnsureDirectory(string path) => _files.Add(path);

        public string? FindCompiler() => Compiler;

        public string? FindXvfb() => Xvfb;

        public VkProbeResult Probe()
        {
            ProbeCount++;
            if (ProbeThrows)
                throw new InvalidOperationException("probe failed");
            if (_probeIndex < Probes.Count)
                return Probes[_probeIndex++];
            return new VkProbeResult(true, 0, false, "", "no-probe-queued");
        }

        public VkCommandResult Run(string fileName, string arguments, string? workingDirectory)
        {
            Commands.Add((fileName, arguments, workingDirectory));
            RunEffect?.Invoke(fileName, arguments);
            return new VkCommandResult(0, "");
        }

        public VkLibraryLoad TryLoadGlobalLibrary(string path)
        {
            Dlopens.Add(path);
            return LoadResult;
        }

        public VkVirtualDisplayStart StartVirtualDisplay(int width, int height)
        {
            StartedDisplays++;
            return new VkVirtualDisplayStart(new FakeVirtualDisplay(99), "");
        }

        public bool VirtualDisplayReady(int displayNumber) => true;

        public void Log(string message) => Logs.Add(message);
    }
}
