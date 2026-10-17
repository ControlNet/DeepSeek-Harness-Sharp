using System.ComponentModel;
using System.Diagnostics;

namespace Dsh.AgentAdapters;

/** Whether an adapter can run on this machine; Version is the raw first line of `--version` from the interactive executable. */
public sealed record AgentAvailability(string Kind, string? InteractivePath, string? AcpPath, string? Version)
{
    public bool CanRunInteractive => InteractivePath is not null;

    public bool CanRunAcp => AcpPath is not null;
}

/** Finds agent executables on PATH the way a shell would, without starting a shell. */
public static class AgentExecutableLocator
{
    public static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(15);

    private const string WindowsDefaultExtensions = ".COM;.EXE;.BAT;.CMD";

    /** Absolute path of Executable: returned as is when it already contains a directory, otherwise searched on PATH (with PATHEXT on Windows). */
    public static string? Resolve(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(executable) ? Path.GetFullPath(executable) : null;
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return directories
            .SelectMany(directory => Candidates(executable).Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(IsExecutableFile);
    }

    /** Resolves both commands of Adapter and reads the interactive executable's version (null when it fails or times out). */
    public static async Task<AgentAvailability> ProbeAsync(AgentAdapter adapter, CancellationToken cancellationToken = default)
    {
        var interactive = adapter.Interactive is { } command ? Resolve(command.Executable) : null;
        var acp = adapter.Acp is { } acpCommand ? Resolve(acpCommand.Executable) : null;
        var version = interactive is null ? null : await ReadVersionAsync(interactive, cancellationToken);
        return new AgentAvailability(adapter.Kind, interactive, acp, version);
    }

    private static async Task<string?> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(VersionProbeTimeout);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            process.Start();
            process.StandardInput.Close();
            var errorDrain = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errorDrain;
            return process.ExitCode == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
    }

    private static IEnumerable<string> Candidates(string executable)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(executable))
            return [executable];
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? WindowsDefaultExtensions).Split(';', StringSplitOptions.RemoveEmptyEntries);
        return extensions.Select(extension => $"{executable}{extension.ToLowerInvariant()}");
    }

    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & anyExecute) != 0;
    }
}
