using System.Diagnostics;

namespace Dsh.Gui.Services;

/**
 * Desktop notifications through the platform's own command: notify-send on Linux desktops, osascript on macOS.
 * Where neither exists (or the notification service refuses) nothing is shown; the in-window toasts and the bell still carry the event.
 */
public static class DesktopNotifier
{
    private const string NotifySend = "notify-send";
    private const string OsaScript = "/usr/bin/osascript";
    private const string ApplicationName = "DeepSeek Harness";

    /** Title and body are passed as arguments, never through a shell, so they need no quoting. */
    private static readonly string[] AppleScript = ["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run"];

    /** The sender for this platform, or null when it has no notification command. */
    public static Action<string, string>? Create()
    {
        if (OperatingSystem.IsMacOS() && File.Exists(OsaScript))
            return (title, body) => Run(OsaScript, [.. AppleScript, title, body]);
        if (OperatingSystem.IsLinux() && FindOnPath(NotifySend) is { } notifySend)
            return (title, body) => Run(notifySend, ["--app-name", ApplicationName, title, body]);
        return null;
    }

    private static string? FindOnPath(string command)
        => (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, command))
            .FirstOrDefault(File.Exists);

    private static void Run(string fileName, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // The notification command is gone or cannot run: the event is still in the bell.
        }
    }
}
