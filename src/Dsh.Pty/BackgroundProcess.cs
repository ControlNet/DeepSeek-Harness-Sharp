using System.Diagnostics;

namespace Dsh.Pty;

/** Starts a daemon without inheriting the caller's terminal or standard streams, using the existing platform process primitives. */
public static class BackgroundProcess
{
    /** Returns the child's lifetime, not a successful-start notification. */
    public static Task Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environment)
    {
        if (OperatingSystem.IsWindows())
            return WaitForWindowsExitAsync(WindowsProcessLaunch.StartWithoutInheritedHandles(fileName, arguments, workingDirectory, environment));

        var pid = UnixPtyNative.SpawnBackground(new PtyStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Environment = environment,
        });
        return UnixPtySession.WaitForExitAsync(pid);
    }

    private static async Task WaitForWindowsExitAsync(int pid)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return;
        }
        using (process)
            await process.WaitForExitAsync().ConfigureAwait(false);
    }
}
