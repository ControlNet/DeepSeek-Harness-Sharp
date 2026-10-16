using System.ComponentModel;
using System.Diagnostics;
using Dsh.Interaction;
using Dsh.Runtime;

namespace Dsh.Deliverables;

/** /deliverables: lists the session deliverables with absolute paths and opens one with the default application. */
public static class DeliverablesCommand
{
    public const string Name = "deliverables";

    public static IDisposable Register(Context ctx)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = Name,
            Description = "List the files presented as deliverables in this session, or open one with the default application",
            Input = new CommandInputDescriptor("[open <number>]"),
            Handler = Handle,
        });
    }

    private static async Task<CommandResult> Handle(CommandInvocation invocation)
    {
        var files = SessionDeliverables.Collect(invocation.Agent.Session.SnapshotEvents());
        var arguments = invocation.RawInput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (arguments)
        {
            case []:
                return new CommandResult.Success(Listing(files));
            case ["open", var number]:
                return int.TryParse(number, out var index) && index >= 1 && index <= files.Count
                    ? await Open(files[index - 1].Path)
                    : new CommandResult.Error($"no deliverable #{number}; run /{Name} to list them");
            default:
                return new CommandResult.Error($"usage: /{Name} [open <number>]");
        }
    }

    private static string Listing(IReadOnlyList<PresentedFile> files)
    {
        if (files.Count == 0)
            return "no deliverables in this session";
        var lines = files.Select((file, index) =>
            $"{index + 1}. {file.Path}{(file.Description is { } description ? $" - {description}" : "")}{(File.Exists(file.Path) ? "" : " (missing)")}");
        return $"deliverables ({files.Count}):\n{string.Join('\n', lines)}\nopen one with /{Name} open <number>";
    }

    // Shell association lookups can block on Windows, so the launch leaves the caller's thread.
    private static async Task<CommandResult> Open(string path)
    {
        if (!File.Exists(path))
            return new CommandResult.Error($"{path} no longer exists");
        try
        {
            await Task.Run(() => StartDefaultApplication(path));
            return new CommandResult.Success($"opened {path}");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return new CommandResult.Error($"cannot open {path}: {error.Message}");
        }
    }

    // The opener never inherits the terminal: xdg-open started from the TUI ran its handler on the TUI's own PTY.
    private static void StartDefaultApplication(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var shell = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }
        var opener = new Process
        {
            StartInfo = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
            {
                ArgumentList = { path },
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        opener.Exited += (_, _) => opener.Dispose();
        try
        {
            opener.Start();
        }
        catch
        {
            opener.Dispose();
            throw;
        }
        opener.StandardInput.Close();
        opener.BeginOutputReadLine();
        opener.BeginErrorReadLine();
    }
}
