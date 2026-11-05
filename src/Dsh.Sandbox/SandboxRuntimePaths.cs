using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Dsh.Sandbox;

/// <summary>Read-only code and runtime directory roots used by sandboxed processes.</summary>
public static class SandboxRuntimePaths
{
    private static readonly Lazy<IReadOnlyList<string>> CurrentHost = new(BuildCurrentHost);
    private static readonly string? UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
        ? Path.TrimEndingDirectorySeparator(global::Dsh.Runtime.CanonicalFilePath.Canonicalize(home)) : null;

    public static IReadOnlyList<string> ForCurrentHost() => CurrentHost.Value;

    public static IReadOnlyList<string> ForExecutable(string executable)
    {
        var paths = NewSet();
        paths.UnionWith(ForCurrentHost());
        var full = Path.GetFullPath(executable);
        paths.Add(full);
        WindowsIoStageTiming.RuntimeRoot(full, WindowsIoStageTiming.RootCategory.Executable);
        var resolved = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        paths.Add(resolved);
        WindowsIoStageTiming.RuntimeRoot(resolved, WindowsIoStageTiming.RootCategory.Executable);
        var muxer = Path.GetFileNameWithoutExtension(resolved).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (!muxer)
        {
            AddDirectory(paths, Path.GetDirectoryName(full), WindowsIoStageTiming.RootCategory.Executable);
            AddDirectory(paths, Path.GetDirectoryName(resolved), WindowsIoStageTiming.RootCategory.Executable);
        }
        var dotnet = muxer ? Path.GetDirectoryName(resolved) : DotnetRoot();
        AddDotnet(paths, dotnet, includeSdk: true);
        return paths.ToArray();
    }

    private static IReadOnlyList<string> BuildCurrentHost()
    {
        var paths = NewSet();
        if (Environment.ProcessPath is { } executable)
        {
            var full = Path.GetFullPath(executable);
            paths.Add(full);
            WindowsIoStageTiming.RuntimeRoot(full, WindowsIoStageTiming.RootCategory.Host);
        }
        AddDirectory(paths, AppContext.BaseDirectory);
        AddDirectory(paths, RuntimeEnvironment.GetRuntimeDirectory());
        AddDotnet(paths, DotnetRoot(), includeSdk: false);
        if (!OperatingSystem.IsWindows())
        {
            foreach (var path in new[] { "/bin", "/usr/bin", "/sbin", "/usr/sbin", "/lib", "/lib64", "/usr/lib", "/usr/lib64" })
                AddDirectory(paths, path);
            if (OperatingSystem.IsMacOS())
            {
                AddDirectory(paths, "/System/Library/Frameworks");
                AddDirectory(paths, "/System/Library/PrivateFrameworks");
                if (File.Exists("/usr/lib/dyld"))
                    paths.Add("/usr/lib/dyld");
            }
        }
        if (SandboxedProcess.BackendExecutablePath is { } backend)
        {
            paths.Add(backend);
            WindowsIoStageTiming.RuntimeRoot(backend, WindowsIoStageTiming.RootCategory.Host);
        }
        return paths.ToArray();
    }

    private static void AddDotnet(HashSet<string> paths, string? root, bool includeSdk)
    {
        if (root is null)
            return;
        foreach (var child in includeSdk ? new[] { "host", "shared", "sdk", "packs", "sdk-manifests" } : new[] { "host", "shared" })
            AddDirectory(paths, Path.Combine(root, child), child switch
            {
                "sdk" => WindowsIoStageTiming.RootCategory.Sdk,
                "packs" => WindowsIoStageTiming.RootCategory.Packs,
                "sdk-manifests" => WindowsIoStageTiming.RootCategory.Manifests,
                "shared" => WindowsIoStageTiming.RootCategory.Shared,
                _ => WindowsIoStageTiming.RootCategory.Host
            });
        var muxer = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(muxer))
        {
            paths.Add(muxer);
            WindowsIoStageTiming.RuntimeRoot(muxer, WindowsIoStageTiming.RootCategory.Executable);
        }
    }

    private static void AddDirectory(HashSet<string> paths, string? directory,
        WindowsIoStageTiming.RootCategory category = WindowsIoStageTiming.RootCategory.Host)
    {
        if (directory is null || !Directory.Exists(directory))
            return;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var canonical = global::Dsh.Runtime.CanonicalFilePath.Canonicalize(full);
        if (paths.Comparer.Equals(canonical, Path.GetPathRoot(canonical))
            || UserHome is not null && paths.Comparer.Equals(canonical, UserHome))
            return;
        paths.Add(full);
        WindowsIoStageTiming.RuntimeRoot(full, category);
    }

    public static string? DotnetRoot()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return null;
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        return runtime.Parent is { Name: "Microsoft.NETCore.App", Parent: { Name: "shared", Parent: { } root } }
            ? root.FullName : null;
    }

    private static HashSet<string> NewSet()
        => new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
