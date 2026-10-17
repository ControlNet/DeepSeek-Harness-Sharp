namespace Dsh.Pty;

/** Starts a daemon without inheriting the caller's terminal or standard streams, using the existing platform process primitives. */
public static class BackgroundProcess
{
    public static int Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environment)
    {
        if (OperatingSystem.IsWindows())
            return WindowsProcessLaunch.StartWithoutInheritedHandles(fileName, arguments, workingDirectory, environment);

        var pid = UnixPtyNative.SpawnBackground(new PtyStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Environment = environment,
        });
        _ = UnixPtySession.WaitForExitAsync(pid);
        return pid;
    }
}
