using System.Diagnostics;
using System.Text;

namespace Dsh.Tests;

/**
 * Real `dsharp host serve` processes of this worktree's build, each under its own harness home (DSH_HOME), so that tests never touch ~/.dsh or another worktree's daemons.
 * The host starts a PTY daemon on demand with the same DSH_HOME; cleanup therefore kills every process whose environment carries that home (Linux /proc).
 */
internal static class RealHostProcesses
{
    private const string HomeVariable = "DSH_HOME";

    /** Settings for a throwaway home: no file logging, so the directory can be deleted while handles are still closing. */
    private const string Settings = """
        logging:
          file: false
        """;

    public static string? Executable => PtyTuiHarness.FindTuiExecutable();

    /** A short home under the repository (socket paths are limited to 108 bytes) with throwaway settings. */
    public static string CreateHome(string prefix)
    {
        var root = TempTree.CreateSocketDirectory(prefix);
        File.WriteAllText(Path.Combine(root, "settings.yaml"), Settings);
        return root;
    }

    public static Process StartHost(string executable, string root)
    {
        var info = new ProcessStartInfo(executable, ["host", "serve"])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        info.Environment[HomeVariable] = root;
        var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start dsharp host serve");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /** Kills every process of this user whose environment has DSH_HOME=root (hosts, PTY daemons, the shells they run). */
    public static void KillAll(string root)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var marker = Encoding.UTF8.GetBytes($"{HomeVariable}={root}\0");
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(directory), out var pid) && pid != Environment.ProcessId && EnvironmentContains(directory, marker))
                TryKill(pid);
        }
    }

    private static bool EnvironmentContains(string processDirectory, byte[] marker)
    {
        try
        {
            return File.ReadAllBytes(Path.Combine(processDirectory, "environ")).AsSpan().IndexOf(marker) >= 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
