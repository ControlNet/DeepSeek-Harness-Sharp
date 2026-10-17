namespace Dsh.Tests;

/**
 * The shell that end-to-end tests run in real terminal sessions: cmd.exe with delayed expansion on Windows, /bin/sh elsewhere.
 * Lines print "<label>-42" with the shell's own arithmetic, so the typed input never contains the text a test waits for.
 */
internal static class TestShell
{
    public static string FileName => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    /** /v:on makes cmd expand !var! when the command runs instead of when the line is parsed. */
    public static string[] Arguments => OperatingSystem.IsWindows() ? ["/v:on"] : [];

    /** A line that prints label-42. */
    public static string EchoAnswer(string label)
        => OperatingSystem.IsWindows() ? $"set /a n=6*7 & echo {label}-!n!" : $"echo {label}-$((6*7))";

    /** A line that keeps the shell busy for the given seconds or until Ctrl+C (cmd has no sleep; ping waits one second between echoes). */
    public static string Wait(int seconds)
        => OperatingSystem.IsWindows() ? $"ping -n {seconds + 1} 127.0.0.1 > nul" : $"sleep {seconds}";

    /** One line that runs first and then second. */
    public static string Sequence(string first, string second)
        => OperatingSystem.IsWindows() ? $"{first} & {second}" : $"{first}; {second}";

    /** Arguments that run line non-interactively and then keep the session alive for the given seconds. */
    public static string[] RunThenWait(string line, int seconds)
        => [.. Arguments, OperatingSystem.IsWindows() ? "/c" : "-c", Sequence(line, Wait(seconds))];
}
