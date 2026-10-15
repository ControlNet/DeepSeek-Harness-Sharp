namespace Dsh.AgentAdapters;

/**
 * Built-in adapters plus factories for user-defined agents.
 * Every command line and screen pattern below was checked on 2026-10-15 against the installed CLI (`--help`, live TUI captures)
 * and the vendor sources:
 * https://learn.chatgpt.com/docs/developer-commands?surface=cli, https://code.claude.com/docs/en/cli-reference,
 * https://opencode.ai/docs/cli/, https://opencode.ai/docs/acp/, https://github.com/agentclientprotocol (codex-acp, claude-agent-acp).
 */
public static class AgentAdapterCatalog
{
    /** npm runner used for ACP bridges that ship as npm packages (resolved to npx.cmd on Windows by AgentExecutableLocator). */
    public const string NpxExecutable = "npx";

    /** ACP bridge for Codex (Codex has no native ACP mode); replaces the deprecated @zed-industries/codex-acp. */
    public const string CodexAcpPackage = "@agentclientprotocol/codex-acp";

    /** ACP bridge for Claude Code (no native ACP mode); replaces the deprecated @zed-industries/claude-code-acp. */
    public const string ClaudeAcpPackage = "@agentclientprotocol/claude-agent-acp";

    /** Codex CLI 0.162: `codex [PROMPT]`, `codex resume <id>|--last`; the TUI uses the process working directory (`-C` is optional). */
    public static AgentAdapter Codex { get; } = new(
        AgentKinds.Codex,
        "Codex",
        new AgentCommand("codex", []),
        new AgentCommand(NpxExecutable, ["-y", CodexAcpPackage]),
        new AgentLaunchSyntax(
            Prompt: [AgentLaunchSyntax.PromptToken],
            ResumeById: ["resume", AgentLaunchSyntax.SessionIdToken],
            ResumeLatest: ["resume", "--last"]),
        AgentScreenRules.FromPatterns(
            approval:
            [
                @"Would you like to run the following command\?",
                @"Would you like to make the following edits\?",
                "Press enter to confirm or esc to cancel",
                @"Trust this folder\?",
            ],
            busy: ["esc to interrupt"],
            idle: [@"^\s*› ", @"\? for shortcuts"]));

    /** Claude Code 2.1: `claude [prompt]`, `claude --resume <id>`, `claude --continue`; there is no top-level working directory flag. */
    public static AgentAdapter ClaudeCode { get; } = new(
        AgentKinds.ClaudeCode,
        "Claude Code",
        new AgentCommand("claude", []),
        new AgentCommand(NpxExecutable, ["-y", ClaudeAcpPackage]),
        new AgentLaunchSyntax(
            Prompt: [AgentLaunchSyntax.PromptToken],
            ResumeById: ["--resume", AgentLaunchSyntax.SessionIdToken],
            ResumeLatest: ["--continue"]),
        AgentScreenRules.FromPatterns(
            approval:
            [
                @"Do you want to proceed\?",
                "Esc to cancel · Tab to amend",
                @"Is this a project you created or one you trust\?",
                "Enter to confirm · Esc to cancel",
            ],
            busy: ["esc to interrupt"],
            idle: [@"^\s*❯"]));

    /** OpenCode 1.18: `opencode [project]`, `--prompt`, `--session <id>`, `--continue`; native ACP via `opencode acp`. */
    public static AgentAdapter OpenCode { get; } = new(
        AgentKinds.OpenCode,
        "OpenCode",
        new AgentCommand("opencode", []),
        new AgentCommand("opencode", ["acp"]),
        new AgentLaunchSyntax(
            Prompt: ["--prompt", AgentLaunchSyntax.PromptToken],
            ResumeById: ["--session", AgentLaunchSyntax.SessionIdToken],
            ResumeLatest: ["--continue"]),
        AgentScreenRules.FromPatterns(
            approval: ["Permission required", @"Allow once\s+Allow always\s+Reject"],
            busy: ["esc interrupt"],
            idle: [@"ctrl\+p commands"]));

    public static IReadOnlyList<AgentAdapter> BuiltIn { get; } = [Codex, ClaudeCode, OpenCode];

    public static AgentAdapter? Find(string kind) => BuiltIn.FirstOrDefault(adapter => adapter.Kind == kind);

    /** Unknown agent: any command run as a raw terminal session, without attention rules. */
    public static AgentAdapter Terminal(string displayName, AgentCommand command)
        => new(AgentKinds.Terminal, displayName, command, null, AgentLaunchSyntax.None, AgentScreenRules.None);

    /** Any other ACP agent, driven only through its stdio ACP command. */
    public static AgentAdapter GenericAcp(string displayName, AgentCommand acpCommand)
        => new(AgentKinds.Acp, displayName, null, acpCommand, AgentLaunchSyntax.None, AgentScreenRules.None);
}
