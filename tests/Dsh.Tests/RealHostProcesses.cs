using System.Diagnostics;
using System.Text;
using Dsh.Boot;

namespace Dsh.Tests;

/**
 * Real `dsharp host serve` processes of this worktree's build, each under its own harness home (DSH_HOME), so that tests never touch ~/.dsh or another worktree's daemons.
 * The host starts a PTY daemon on demand with the same DSH_HOME; cleanup therefore kills every process whose environment carries that home (Linux /proc).
 */
internal static class RealHostProcesses
{
    private const string HomeVariable = HarnessHome.HomeEnv;

    /** Settings for a throwaway home: no file logging, so the directory can be deleted while handles are still closing. */
    private const string Settings = """
        logging:
          file: false
        """;

    /** This worktree's dsharp; CI builds only the test project, so without it the test is skipped (as PtyTuiHarness and the template plugin tests do). */
    public static string Executable
    {
        get
        {
            var executable = PtyTuiHarness.FindTuiExecutable();
            if (executable is null)
                Assert.Skip("dsharp has not been built (DeepSeek-Harness-Sharp/bin/Debug/net10.0)");
            return executable;
        }
    }

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

/** Runs the fleet CLI (`dsharp machine|session|doctor`) of this build as a separate process under a given harness home. */
internal static class FleetCliProcess
{
    /** Remote commands may deploy or start a host first. */
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public sealed record Result(int ExitCode, string Output, string Error);

    public static async Task<Result> RunAsync(string executable, string home, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = home,
        };
        info.Environment[HarnessHome.HomeEnv] = home;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start dsharp");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return new Result(process.ExitCode, await output, await error);
    }

    /** The cells of each table line the CLI prints (cells never contain spaces in these tests). */
    public static List<string[]> Rows(string output)
        => [.. output.Split('\n').Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))];
}
