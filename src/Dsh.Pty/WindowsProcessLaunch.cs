using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Dsh.Pty;

/**
 * Windows process creation shared by ConPTY sessions and detached daemons: the command line and environment block CreateProcess takes, and a start that hands the child no handles.
 * Process.Start lets a child inherit every inheritable handle; a daemon started that way by a CLI whose output is a pipe kept the pipe open, so the reader of the CLI's output never saw its end.
 */
public static class WindowsProcessLaunch
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    /** The program and its arguments quoted for CreateProcess. */
    public static string CommandLine(string fileName, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        AppendQuoted(builder, fileName);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendQuoted(builder, argument);
        }
        return builder.ToString();
    }

    /** This process's environment with the overrides applied (a null value removes the variable), as a Unicode block the caller frees with Marshal.FreeHGlobal. */
    public static IntPtr EnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            merged[(string)entry.Key] = (string?)entry.Value;
        foreach (var (key, value) in overrides)
            merged[key] = value;
        var builder = new StringBuilder();
        foreach (var (key, value) in merged)
        {
            if (value is null)
                continue;
            builder.Append(key).Append('=').Append(value).Append('\0');
        }
        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    /** Starts a console program detached: without a window, without inheriting any handle, in its own console. Returns its process id. */
    [SupportedOSPlatform("windows")]
    public static int StartWithoutInheritedHandles(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environmentOverrides)
    {
        var environment = EnvironmentBlock(environmentOverrides);
        try
        {
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            // CreateProcessW may write into the command line buffer, so it gets a mutable copy.
            var commandLine = new StringBuilder(CommandLine(fileName, arguments));
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow | CreateUnicodeEnvironment, environment, workingDirectory, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"failed to start {fileName}");
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            return process.dwProcessId;
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }

    private static void AppendQuoted(StringBuilder builder, string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"'))
        {
            builder.Append(value);
            return;
        }
        builder.Append('"');
        builder.Append(value.Replace("\\\"", "\\\\\"", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal));
        builder.Append('"');
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref StartupInfo lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
