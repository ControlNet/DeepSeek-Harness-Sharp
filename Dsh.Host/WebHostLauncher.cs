using System.ComponentModel;
using System.Diagnostics;

namespace Dsh.Host;

/** Keeps `dsharp web` as a process entry only; the TUI/GUI dependency graph never references the web application or ASP.NET. */
internal static class WebHostLauncher
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var executable = OperatingSystem.IsWindows() ? "dsharp-web.exe" : "dsharp-web";
        var installed = Path.Combine(AppContext.BaseDirectory, executable);
        var info = new ProcessStartInfo(File.Exists(installed) ? installed : executable) { UseShellExecute = false };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start dsharp-web");
            // The child shares this terminal and receives Ctrl+C itself. Keep the launcher alive to preserve its exit code.
            ConsoleCancelEventHandler cancel = (_, eventArgs) => eventArgs.Cancel = true;
            Console.CancelKeyPress += cancel;
            try { await process.WaitForExitAsync().ConfigureAwait(false); }
            finally { Console.CancelKeyPress -= cancel; }
            return process.ExitCode;
        }
        catch (Win32Exception error)
        {
            await Console.Error.WriteLineAsync($"dsharp: cannot start the independent web host: {error.Message}. Install dsharp-web beside dsharp or on PATH, or run `dotnet run --project Dsh.WebHost --` from the source tree.");
            return 1;
        }
    }
}
