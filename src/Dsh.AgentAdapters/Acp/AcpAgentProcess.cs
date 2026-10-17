using System.Diagnostics;
using System.Text;
using Dsh.Acp;
using Dsh.Sdk;

namespace Dsh.AgentAdapters.Acp;

/**
 * An external ACP agent running as a child process, talking ACP over its stdin/stdout.
 * stderr is drained continuously (agents log there, and a full pipe would stall them) and its tail kept for diagnostics.
 */
public sealed class AcpAgentProcess : IAsyncDisposable
{
    /** How long the agent gets to exit by itself after its stdin closes before it is killed. */
    public static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(3);

    private const int StandardErrorTailChars = 16 * 1024;

    private readonly Process _process;
    private readonly StringBuilder _standardError = new();
    private readonly Lock _standardErrorLock = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AcpAgentProcess(Process process)
    {
        _process = process;
        var transport = new JsonRpcLineTransport(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        Client = new AcpClient(transport);
        transport.Start();
    }

    public AcpClient Client { get; }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    /** Completes with the exit code once the agent process has exited (killed by DisposeAsync or on its own). */
    public Task<int> Exited => _exited.Task;

    /** The most recent stderr output of the agent, bounded to a few kilobytes. */
    public string StandardErrorTail
    {
        get
        {
            lock (_standardErrorLock)
                return _standardError.ToString();
        }
    }

    /**
     * Starts Command in WorkingDirectory; Environment entries are added to (or override) the inherited environment.
     * Throws FileNotFoundException when the executable is not on PATH.
     */
    public static AcpAgentProcess Start(
        AgentCommand command,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var executable = AgentExecutableLocator.Resolve(command.Executable)
                         ?? throw new FileNotFoundException($"agent executable not found on PATH: {command.Executable}", command.Executable);
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.Arguments)
            info.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            info.Environment[name] = value;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Start();
        var agent = new AcpAgentProcess(process);
        process.ErrorDataReceived += (_, line) => agent.AppendStandardError(line.Data);
        process.Exited += (_, _) => agent.RecordExit();
        process.BeginErrorReadLine();
        if (process.HasExited)
            agent.RecordExit();
        return agent;
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _process.WaitForExitAsync(cancellationToken);

    /** The Exited event may still be in flight when DisposeAsync releases the process; a disposed process has no exit code to read. */
    private void RecordExit()
    {
        try
        {
            _exited.TrySetResult(_process.ExitCode);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void AppendStandardError(string? line)
    {
        if (line is null)
            return;
        lock (_standardErrorLock)
        {
            _standardError.AppendLine(line);
            var overflow = _standardError.Length - StandardErrorTailChars;
            if (overflow > 0)
                _standardError.Remove(0, overflow);
        }
    }

    /** Closes the agent's stdin (its cue to exit), waits briefly, then kills the whole process tree if it is still running. */
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        using var grace = new CancellationTokenSource(ExitGracePeriod);
        try
        {
            await _process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        RecordExit();
        _process.Dispose();
    }
}
