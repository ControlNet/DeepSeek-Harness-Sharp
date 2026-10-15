using Dsh.AgentAdapters;

namespace Dsh.Tests;

/**
 * Screen fixtures are tmux captures of the real TUIs (codex-cli 0.162.1, Claude Code 2.1.300, OpenCode 1.18.33, 120x40) taken on 2026-10-15;
 * only the project path was shortened.
 */
public sealed class AgentAdapterTests
{
    private const string CodexIdle = """
        › Ask Codex to do anything
          GPT-6-Astra medium · ~/project · Context 0% used · 0 out · 0 …
          ← for agents · ? for shortcuts                                                               ⚠ 1 warning · f2 to view
        """;

    private const string CodexBusy = """
        › Run the shell command: rm -f nonexistent-probe-file.txt   (just run it once, then reply with the single word done)
        • This command will delete nonexistent-probe-file.txt if it exists.
        • Working (4s • esc to interrupt)
        › Ask Codex to do anything
          GPT-6-Astra medium · ~/project · Context 3% used · 56 out · 1…
          ← for agents · ? for shortcuts                                                               ⚠ 1 warning · f2 to view
        """;

    private const string CodexApproval = """
        › Create an empty file named probe.txt by running the shell command: touch probe.txt
        • I’ll run touch probe.txt in the current directory.
        • Running touch probe.txt
          Would you like to run the following command?
          Environment: local
          Reason: May I run touch probe.txt to create the requested empty file? The workspace is read-only, so this requires
          write approval.
          $ touch probe.txt
        › 1. Yes, proceed (y)
          2. Yes, and don't ask again for commands that start with `touch probe.txt` (p)
          3. No, and tell Codex what to do differently (esc)
          Press enter to confirm or esc to cancel
        """;

    private const string CodexTrust = """
          Folder access
          ~/project
          Trust this folder? Codex can read, edit, and run files here, subject to your permission settings. Folder settings
          can run code automatically, even without a model request. Continue only if you trust these files. Your trust
          decision will be saved.
        › 1. Trust and continue
          2. Back to Agent Command Center
          enter continue · esc back
        """;

    private const string ClaudeIdle = """
        ❯ Run the shell command: rm -f nonexistent-probe-file.txt   (just run it once, then reply with the single word done)
          Ran 1 shell command
        ● done
        ✻ Cooked for 9s · done 9:32 am
        ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        ❯
        ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
          ⏵⏵ auto mode on (shift+tab to cycle) · ← for agents
        """;

    private const string ClaudeBusy = """
        ❯ Run the shell command: rm -f nonexistent-probe-file.txt   (just run it once, then reply with the single word done)
          Removing probe file if it exists
          ⎿  $ rm -f nonexistent-probe-file.txt
        ✽ Canoodling… (4s · ↓ 72 tokens)
        ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        ❯
        ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
          ⏵⏵ auto mode on (shift+tab to cycle) · esc to interrupt · ← for agents
        """;

    private const string ClaudeApproval = """
          Creating empty probe.txt file
          ⎿  $ touch probe.txt && ls -la probe.txt
        ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
         Bash command
         Tip: auto mode handles these prompts for you — choose "switch to auto mode" below
         Create empty probe.txt file
         touch probe.txt && ls -la probe.txt
         Do you want to proceed?
         ❯ 1. Yes
           2. Yes, and always allow access to ~/project from
              this project
           3. Yes, and switch to auto mode · auto mode handles these prompts for you
           4. No
         Esc to cancel · Tab to amend
        """;

    private const string ClaudeTrust = """
         Accessing workspace:
         ~/project
         Quick safety check: Is this a project you created or one you trust? (Like your own code, a well-known open source
         project, or work from your team). If not, take a moment to review what's in this folder first.
         Claude Code'll be able to read, edit, and execute files here.
         Security guide
         ❯ No, exit
           Yes, I trust this folder
         Enter to confirm · Esc to cancel
        """;

    private const string OpenCodeIdle = """
          ┃
             done
             ▣  Sisyphus - Ultraworker · GPT-6.1 Sol · 9.5s
          ┃
          ┃  Sisyphus - Ultraworker · GPT-6.1 Sol OpenAI · high
          ╹▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀
           ~/project                                                                  23.3K (6%)  ctrl+p commands
        """;

    private const string OpenCodeBusy = """
          ┃  Run the shell command: rm -f nonexistent-probe-file.txt   (just run it once, then reply with the single word
          ┃  done)
             ▣  Sisyphus - Ultraworker · GPT-6.1 Sol
          ┃  Sisyphus - Ultraworker · GPT-6.1 Sol OpenAI · high
          ╹▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀
           ⬝⬝⬝⬝⬝⬝⬝⬝  esc interrupt                                                                 tab agents  ctrl+p commands
        """;

    private const string OpenCodeApproval = """
          ┃  Create an empty file named probe2.txt by running the shell command: touch probe2.txt
             $ ls -ld .
             ▣  Sisyphus - Ultraworker · GPT-6.1 Sol
          ┃  △ Permission required
          ┃    # Shell command
          ┃  $ ls -ld .
          ┃   Allow once   Allow always   Reject                                 ctrl+f fullscreen  ⇆ select  enter confirm
        """;

    public static TheoryData<string, string, AgentScreenState> Screens => new()
    {
        { AgentKinds.Codex, CodexIdle, AgentScreenState.WaitingForInput },
        { AgentKinds.Codex, CodexBusy, AgentScreenState.Busy },
        { AgentKinds.Codex, CodexApproval, AgentScreenState.WaitingForApproval },
        { AgentKinds.Codex, CodexTrust, AgentScreenState.WaitingForApproval },
        { AgentKinds.ClaudeCode, ClaudeIdle, AgentScreenState.WaitingForInput },
        { AgentKinds.ClaudeCode, ClaudeBusy, AgentScreenState.Busy },
        { AgentKinds.ClaudeCode, ClaudeApproval, AgentScreenState.WaitingForApproval },
        { AgentKinds.ClaudeCode, ClaudeTrust, AgentScreenState.WaitingForApproval },
        { AgentKinds.OpenCode, OpenCodeIdle, AgentScreenState.WaitingForInput },
        { AgentKinds.OpenCode, OpenCodeBusy, AgentScreenState.Busy },
        { AgentKinds.OpenCode, OpenCodeApproval, AgentScreenState.WaitingForApproval },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public void ScreenRules_ClassifyCapturedScreens(string kind, string screen, AgentScreenState expected)
    {
        var adapter = AgentAdapterCatalog.Find(kind)!;

        Assert.Equal(expected, adapter.ScreenRules.Classify(screen.Split('\n')));
    }

    [Fact]
    public void ScreenRules_ForRawTerminal_NeverClaimAState()
    {
        var terminal = AgentAdapterCatalog.Terminal("my agent", new AgentCommand("my-agent", ["--flag"]));

        Assert.Equal(AgentScreenState.Unknown, terminal.ScreenRules.Classify(ClaudeApproval.Split('\n')));
        Assert.Equal(new AgentCommand("my-agent", ["--flag"]), terminal.InteractiveCommand("ignored prompt"));
        Assert.Null(terminal.ResumeCommand("id"));
        Assert.Null(terminal.Acp);
    }

    [Fact]
    public void Commands_ExpandPromptAndResumeTemplates()
    {
        Assert.Equal(["fix the tests"], AgentAdapterCatalog.Codex.InteractiveCommand("fix the tests")!.Arguments);
        Assert.Equal(["resume", "abc"], AgentAdapterCatalog.Codex.ResumeCommand("abc")!.Arguments);
        Assert.Equal(["resume", "--last"], AgentAdapterCatalog.Codex.ResumeCommand()!.Arguments);
        Assert.Equal(["--resume", "abc"], AgentAdapterCatalog.ClaudeCode.ResumeCommand("abc")!.Arguments);
        Assert.Equal(["--continue"], AgentAdapterCatalog.ClaudeCode.ResumeCommand()!.Arguments);
        Assert.Equal(["--prompt", "fix the tests"], AgentAdapterCatalog.OpenCode.InteractiveCommand("fix the tests")!.Arguments);
        Assert.Equal(["--session", "ses_1"], AgentAdapterCatalog.OpenCode.ResumeCommand("ses_1")!.Arguments);
        Assert.Equal(new AgentCommand("opencode", ["acp"]), AgentAdapterCatalog.OpenCode.Acp);
        Assert.Empty(AgentAdapterCatalog.OpenCode.InteractiveCommand()!.Arguments);
    }

    [Fact]
    public void GenericAcp_HasNoInteractiveMode()
    {
        var agent = AgentAdapterCatalog.GenericAcp("Gemini", new AgentCommand("gemini", ["--experimental-acp"]));

        Assert.Equal((AgentKinds.Acp, (AgentCommand?)null), (agent.Kind, agent.InteractiveCommand("hi")));
        Assert.Equal("gemini", agent.Acp!.Executable);
    }

    [Fact]
    public void Locator_ResolvesExecutablesOnPathOnly()
    {
        Assert.NotNull(AgentExecutableLocator.Resolve("git"));
        Assert.Null(AgentExecutableLocator.Resolve($"dsh-missing-{Guid.NewGuid():N}"));
        Assert.Null(AgentExecutableLocator.Resolve(Path.Combine(AppContext.BaseDirectory, "missing", "agent")));
    }

    [Fact]
    public async Task Locator_ProbesInstalledAgentVersion()
    {
        if (AgentExecutableLocator.Resolve("opencode") is null)
            Assert.Skip("opencode is not installed on this machine");

        var availability = await AgentExecutableLocator.ProbeAsync(AgentAdapterCatalog.OpenCode, TestContext.Current.CancellationToken);

        Assert.True(availability.CanRunInteractive && availability.CanRunAcp);
        Assert.Matches(@"^\d+\.\d+", availability.Version);
    }
}
