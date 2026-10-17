namespace Dsh.RemoteHost;

/** How an external agent runs: its full-screen TUI inside a PTY, or its ACP channel over stdio. */
public static class RemoteAgentModes
{
    public const string Terminal = "terminal";
    public const string Acp = "acp";
}

/**
 * One startable agent kind on this host (host.agent.catalog).
 * AgentKind is what host.agent.start takes; SessionKind is the kind the started session is registered under in the default mode.
 * RequiresCommand marks the user-defined kinds (raw terminal, generic ACP) that need RemoteAgentStartRequest.Command.
 */
public sealed record RemoteAgentCatalogEntry(
    string AgentKind,
    string DisplayName,
    string SessionKind,
    bool Available,
    bool SupportsTerminal,
    bool SupportsAcp,
    bool SupportsResume,
    bool RequiresCommand,
    string? InteractivePath = null,
    string? AcpPath = null,
    string? Version = null);

/** Executable plus arguments for a user-defined agent; an argument equal to "{prompt}" is replaced by the initial prompt. */
public sealed record RemoteAgentCommand(string Executable, IReadOnlyList<string> Arguments);

/**
 * Optional git worktree for a new session: an existing worktree of the project's repository at Path is reused, otherwise one is added there.
 * NewBranch and StartPoint follow `git worktree add -b <NewBranch> <Path> <StartPoint>`.
 */
public sealed record RemoteWorktreeChoice(string Path, string? NewBranch = null, string? StartPoint = null);

/**
 * host.agent.start: one entry point for every session kind.
 * AgentKind "dsharp" starts a harness session (Selection/Provider as in host.session.create); built-in adapter kinds start that CLI;
 * "terminal" and "acp" run Command. Mode defaults to acp for "acp" and terminal otherwise.
 * Environment is added to the agent process only; terminal status reflects process lifecycle only.
 */
public sealed record RemoteAgentStartRequest(
    string AgentKind,
    string ProjectPath,
    string? InitialPrompt = null,
    string? Mode = null,
    RemoteAgentCommand? Command = null,
    RemoteWorktreeChoice? Worktree = null,
    string? Title = null,
    int? Columns = null,
    int? Rows = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    RemoteModelSelection? Selection = null,
    RemoteProviderSpec? Provider = null);

/** host.agent.prompt: one user turn for an ACP or harness session. */
public sealed record RemoteAgentPrompt(string SessionId, string Text);
