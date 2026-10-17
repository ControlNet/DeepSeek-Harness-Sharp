using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.AgentAdapters;
using Dsh.Core;
using Dsh.GitInspect;
using Dsh.RemoteHost;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Host;

/**
 * How an external agent session was launched, kept so it can be resumed after it exits or the host restarts.
 * Environment may hold credentials the client passed for the agent, so the file is owner-only.
 */
internal sealed record AgentLaunchRecord(
    string SessionId,
    string Mode,
    string AgentKind,
    string Executable,
    IReadOnlyList<string> Arguments,
    string ProjectPath,
    string? Title,
    IReadOnlyDictionary<string, string>? Environment,
    string? AcpSessionId);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<AgentLaunchRecord>))]
internal sealed partial class AgentLaunchJsonContext : JsonSerializerContext;

/**
 * External agent sessions owned by the host (host.agent.* for everything but harness sessions).
 * Terminal mode runs the agent's TUI in the PTY daemon (external session, survives host restarts).
 * ACP mode runs the agent's ACP entry as a child process (acp session, exits with the host and can be resumed).
 * Only sessions started here are known; nothing is scanned or adopted.
 */
internal sealed class ExternalAgentSessions : IAsyncDisposable
{
    private const string RecordsFileName = "agents.json";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly AcpSessionServices _services;
    private readonly HostPtySessions _ptys;
    private readonly string _hostPath;
    private readonly Context _ctx;
    private readonly SessionStore _store;
    private readonly ConcurrentDictionary<string, AgentLaunchRecord> _records;
    private readonly ConcurrentDictionary<string, AcpAgentSession> _live = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<AcpTranscript>>> _transcripts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private ExternalAgentSessions(string hostPath, Context ctx, AcpSessionServices services, HostPtySessions ptys, IEnumerable<AgentLaunchRecord> records)
    {
        _hostPath = hostPath;
        _ctx = ctx;
        _store = ctx.Get<SessionStore>(SessionStore.ServiceName) ?? throw new InvalidOperationException("session store is not registered");
        _services = services;
        _ptys = ptys;
        _records = new ConcurrentDictionary<string, AgentLaunchRecord>(records.Select(record => KeyValuePair.Create(record.SessionId, record)), StringComparer.Ordinal);
    }

    private HostSessionRegistry Registry => _services.Registry;

    /** Loads launch records and restores ACP conversations into the shared session store. */
    public static async Task<ExternalAgentSessions> OpenAsync(string hostPath, Context ctx, AcpSessionServices services, HostPtySessions ptys, CancellationToken cancellationToken)
    {
        var path = Path.Combine(hostPath, RecordsFileName);
        var records = File.Exists(path)
            ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), AgentLaunchJsonContext.Default.ListAgentLaunchRecord)
                ?? throw new JsonException("the agent launch registry must be a JSON array")
            : [];
        var sessions = new ExternalAgentSessions(hostPath, ctx, services, ptys, records);
        foreach (var record in records.Where(record => record.Mode == RemoteAgentModes.Acp))
            await sessions.TranscriptAsync(record).ConfigureAwait(false);
        return sessions;
    }

    /** The harness entry, every built-in adapter with its probed executables, and the two user-defined kinds. */
    public static async Task<IReadOnlyList<RemoteAgentCatalogEntry>> CatalogAsync(CancellationToken cancellationToken)
    {
        var probes = await Task.WhenAll(AgentAdapterCatalog.BuiltIn.Select(adapter => AgentExecutableLocator.ProbeAsync(adapter, cancellationToken))).ConfigureAwait(false);
        var builtIn = AgentAdapterCatalog.BuiltIn.Zip(probes, (adapter, probe) => new RemoteAgentCatalogEntry(
            adapter.Kind, adapter.DisplayName, HostSessionKinds.External, probe.CanRunInteractive || probe.CanRunAcp, probe.CanRunInteractive, probe.CanRunAcp,
            adapter.Syntax.ResumeLatest is not null || probe.CanRunAcp, false, probe.InteractivePath, probe.AcpPath, probe.Version));
        return
        [
            new RemoteAgentCatalogEntry(HostAgentKinds.Dsharp, "DeepSeek Harness", HostSessionKinds.Harness, true, false, false, true, false),
            .. builtIn,
            new RemoteAgentCatalogEntry(AgentKinds.Terminal, "Custom command", HostSessionKinds.External, true, true, false, false, true),
            new RemoteAgentCatalogEntry(AgentKinds.Acp, "Custom ACP agent", HostSessionKinds.Acp, true, false, true, true, true),
        ];
    }

    /**
     * The session's working directory: ProjectPath itself, or a git worktree of its repository.
     * An existing worktree at the requested path is reused; otherwise it is added (relative paths are taken from ProjectPath).
     */
    public static async Task<string> PrepareDirectoryAsync(string projectPath, RemoteWorktreeChoice? worktree, CancellationToken cancellationToken)
    {
        var project = HostPathPolicy.Expand(projectPath);
        if (!Directory.Exists(project))
            throw new DirectoryNotFoundException($"project directory does not exist: {project}");
        if (worktree is null)
            return project;
        var inspector = new GitInspector(project);
        var target = worktree.Path.StartsWith('~') ? HostPathPolicy.Expand(worktree.Path) : Path.GetFullPath(worktree.Path, project);
        var existing = (await inspector.ListWorktreesAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(Path.TrimEndingDirectorySeparator(candidate.Path), Path.TrimEndingDirectorySeparator(target), PathComparison));
        return existing?.Path ?? (await inspector.AddWorktreeAsync(new GitWorktreeAddRequest(target, worktree.NewBranch, worktree.StartPoint), cancellationToken).ConfigureAwait(false)).Path;
    }

    /** Whether SessionId is an ACP session started by this host (its events come from the transcript). */
    public bool IsAcp(string sessionId)
        => _records.TryGetValue(sessionId, out var record) && record.Mode == RemoteAgentModes.Acp;

    public async Task<RemoteHostSession> StartAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken)
    {
        var directory = await PrepareDirectoryAsync(request.ProjectPath, request.Worktree, cancellationToken).ConfigureAwait(false);
        var adapter = AdapterFor(request);
        var mode = request.Mode ?? (request.AgentKind == AgentKinds.Acp ? RemoteAgentModes.Acp : RemoteAgentModes.Terminal);
        var command = mode switch
        {
            RemoteAgentModes.Acp => adapter.Acp ?? throw new NotSupportedException($"{adapter.DisplayName} has no ACP mode"),
            RemoteAgentModes.Terminal => TerminalCommand(adapter, request.InitialPrompt),
            _ => throw new ArgumentException($"unknown agent mode: {mode}"),
        };
        var record = new AgentLaunchRecord(
            "", mode, adapter.Kind, command.Executable, command.Arguments, directory, request.Title ?? adapter.DisplayName, request.Environment, null);
        return mode == RemoteAgentModes.Acp
            ? await StartAcpAsync(record with { SessionId = $"acp-{Guid.NewGuid():N}" }, request.InitialPrompt, cancellationToken).ConfigureAwait(false)
            : await StartTerminalAsync(record, request.Columns, request.Rows, cancellationToken).ConfigureAwait(false);
    }

    public void Prompt(string sessionId, string text) => Live(sessionId).Prompt(text);

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken) => Live(sessionId).CancelAsync(cancellationToken);

    /** Ends the agent process; the registry records the exit (with the exit code the process reported). */
    public async Task StopAsync(string sessionId, CancellationToken cancellationToken)
    {
        var record = Record(sessionId);
        if (record.Mode == RemoteAgentModes.Terminal)
        {
            await _ptys.StopAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!_live.TryRemove(sessionId, out var session))
            return;
        await session.DisposeAsync().ConfigureAwait(false);
        Registry.MarkExited(sessionId, await session.Exited.ConfigureAwait(false));
    }

    /**
     * ACP sessions reopen the agent's own session under the same host session id.
     * Terminal-mode agents start again with their adapter's "continue the latest session" arguments in the same directory and come back
     * as a new session (the PTY daemon names PTYs); the exited record stays as it was.
     */
    public async Task<RemoteHostSession> ResumeAsync(string sessionId, CancellationToken cancellationToken)
    {
        var record = Record(sessionId);
        if (Registry.Get(sessionId) is not { Status: HostSessionStatuses.Exited })
            throw new InvalidOperationException($"session {sessionId} is still running");
        if (record.Mode == RemoteAgentModes.Acp)
            return await StartAcpAsync(record, null, cancellationToken).ConfigureAwait(false);
        var adapter = AgentAdapterCatalog.Find(record.AgentKind);
        var command = adapter?.ResumeCommand() ?? throw new NotSupportedException($"{record.Title ?? record.AgentKind} has no resume arguments");
        return await StartTerminalAsync(record with { Executable = command.Executable, Arguments = command.Arguments }, null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Session> SessionAsync(string sessionId)
        => (await TranscriptAsync(Record(sessionId)).ConfigureAwait(false)).Session;

    private async Task<RemoteHostSession> StartTerminalAsync(AgentLaunchRecord record, int? columns, int? rows, CancellationToken cancellationToken)
    {
        var executable = AgentExecutableLocator.Resolve(record.Executable)
            ?? throw new FileNotFoundException($"agent executable not found on PATH: {record.Executable}", record.Executable);
        var start = new RemotePtyStartRequest(executable, record.Arguments, record.ProjectPath, columns, rows, record.Title);
        var id = await _ptys.StartAgentAsync(start, record.ProjectPath, record.AgentKind, record.Environment, cancellationToken).ConfigureAwait(false);
        await SaveAsync(record with { SessionId = id }).ConfigureAwait(false);
        return Registry.Get(id) ?? throw new InvalidOperationException($"session {id} was not registered");
    }

    /** Registered only after the agent accepted the session, so a failed start leaves nothing behind. */
    private async Task<RemoteHostSession> StartAcpAsync(AgentLaunchRecord record, string? initialPrompt, CancellationToken cancellationToken)
    {
        var transcript = await TranscriptAsync(record).ConfigureAwait(false);
        var command = new AgentCommand(record.Executable, record.Arguments);
        var session = await AcpAgentSession.StartAsync(record.SessionId, command, record.ProjectPath, record.Environment, record.AcpSessionId, transcript, _services, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Registry.Register(new RemoteHostSession(record.SessionId, HostSessionKinds.Acp, record.AgentKind, record.ProjectPath, record.Title, HostSessionStatuses.Idle, now, now, 0, null));
        _live[record.SessionId] = session;
        _ = WatchExitAsync(session);
        await SaveAsync(record with { AcpSessionId = session.AcpSessionId }).ConfigureAwait(false);
        if (initialPrompt is not null)
            session.Prompt(initialPrompt);
        return Registry.Get(record.SessionId)!;
    }

    /** An agent that exits by itself is released here; one stopped through StopAsync was already removed. */
    private async Task WatchExitAsync(AcpAgentSession session)
    {
        var exitCode = await session.Exited.ConfigureAwait(false);
        if (_live.TryRemove(new KeyValuePair<string, AcpAgentSession>(session.SessionId, session)))
            await session.DisposeAsync().ConfigureAwait(false);
        Registry.MarkExited(session.SessionId, exitCode);
    }

    private Task<AcpTranscript> TranscriptAsync(AgentLaunchRecord record)
        => _transcripts.GetOrAdd(record.SessionId, _ => new Lazy<Task<AcpTranscript>>(() => LoadTranscriptAsync(record))).Value;

    private async Task<AcpTranscript> LoadTranscriptAsync(AgentLaunchRecord record)
    {
        var persistence = _ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)
            ?? throw new InvalidOperationException("ACP conversations require session persistence");
        var id = SessionId.Create(record.SessionId);
        Session session;
        if (persistence.Stat(id) is not null)
        {
            using var handle = persistence.Open(id, SessionAccess.Read);
            session = Session.FromRestore(id, handle.Read(), handle.Header, handle.InheritedEventCount);
        }
        else
            session = Session.Create(id, header: HeaderFor(record, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        _store.Enter(session, _ctx);
        try
        {
            _store.Announce(session);
            await _store.Flush(session).ConfigureAwait(false);
            return new AcpTranscript(session, record.AgentKind);
        }
        catch
        {
            _store.Release(id);
            throw;
        }
    }

    private static SessionHeader HeaderFor(AgentLaunchRecord record, long createdAt)
        => new()
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = SessionId.Create(record.SessionId),
            CreatedAt = createdAt,
            Cwd = record.ProjectPath,
            Title = record.Title,
            IsSeeded = false,
        };

    private AcpAgentSession Live(string sessionId)
        => _live.TryGetValue(sessionId, out var session) ? session : throw new InvalidOperationException($"ACP session {sessionId} is not running");

    private AgentLaunchRecord Record(string sessionId)
        => _records.TryGetValue(sessionId, out var record) ? record : throw new InvalidOperationException($"unknown agent session: {sessionId}");

    private static AgentAdapter AdapterFor(RemoteAgentStartRequest request)
    {
        if (AgentAdapterCatalog.Find(request.AgentKind) is { } builtIn)
            return builtIn;
        var command = request.Command is { } custom
            ? new AgentCommand(custom.Executable, custom.Arguments)
            : throw new ArgumentException($"agent kind {request.AgentKind} needs a command");
        var name = request.Title ?? Path.GetFileName(command.Executable);
        return request.AgentKind switch
        {
            AgentKinds.Terminal => AgentAdapterCatalog.Terminal(name, command),
            AgentKinds.Acp => AgentAdapterCatalog.GenericAcp(name, command),
            _ => throw new ArgumentException($"unknown agent kind: {request.AgentKind}"),
        };
    }

    /** Built-in agents take the prompt through their own syntax; a custom command takes it where an argument is exactly "{prompt}". */
    private static AgentCommand TerminalCommand(AgentAdapter adapter, string? prompt)
    {
        if (adapter.Kind != AgentKinds.Terminal)
            return adapter.InteractiveCommand(prompt) ?? throw new NotSupportedException($"{adapter.DisplayName} has no terminal mode");
        var command = adapter.Interactive!;
        if (prompt is not null && !command.Arguments.Contains(AgentLaunchSyntax.PromptToken))
            throw new ArgumentException($"the command takes no initial prompt: put {AgentLaunchSyntax.PromptToken} where it belongs among the arguments");
        return command with
        {
            Arguments = [.. command.Arguments.Where(argument => prompt is not null || argument != AgentLaunchSyntax.PromptToken)
                .Select(argument => argument == AgentLaunchSyntax.PromptToken ? prompt! : argument)],
        };
    }


    /** Whole-file replacement through a temporary file, owner-only because launch environments may carry credentials. */
    private async Task SaveAsync(AgentLaunchRecord record)
    {
        _records[record.SessionId] = record;
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            OwnerOnlyFiles.EnsureDirectory(_hostPath);
            var path = Path.Combine(_hostPath, RecordsFileName);
            var temporary = $"{path}.tmp";
            OwnerOnlyFiles.Write(temporary, JsonSerializer.Serialize([.. _records.Values], AgentLaunchJsonContext.Default.ListAgentLaunchRecord));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /** ACP agents end with the host (their sessions stay resumable); terminal-mode agents stay in the PTY daemon. */
    public async ValueTask DisposeAsync()
    {
        foreach (var id in _live.Keys)
        {
            if (_live.TryRemove(id, out var session))
                await session.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var transcript in _transcripts.Values.Where(lazy => lazy.IsValueCreated))
            _store.Release((await transcript.Value.ConfigureAwait(false)).Session.Id);
        _saveGate.Dispose();
    }
}
