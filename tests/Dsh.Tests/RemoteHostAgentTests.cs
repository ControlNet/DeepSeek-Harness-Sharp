using Dsh.AgentAdapters;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Notifications;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

/**
 * host.agent.* against a real host with real agent processes.
 * Terminal mode runs a local script as an unknown agent, then the installed Codex and Claude Code up to their first screen (no prompt is ever sent to them).
 * ACP mode runs the installed `opencode acp`; only the LlmSmoke tests send prompts to its configured model.
 */
[Collection(SerialProcessCollection.Name)]
public sealed class RemoteHostAgentTests : IDisposable
{
    private static readonly TimeSpan FirstScreen = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Handshake = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Turn = TimeSpan.FromMinutes(4);

    private readonly string _workspace = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../artifacts020/test-tmp", $"agent-{Guid.NewGuid():N}"));

    public RemoteHostAgentTests() => Directory.CreateDirectory(_workspace);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact(Timeout = 60000)]
    public async Task Catalog_ListsTheHarnessTheBuiltInAgentsAndTheCustomKinds()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mac", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("catalog", Ct);

        var catalog = await client.AgentCatalogAsync(Ct);

        Assert.True(client.Supports(HostCapabilities.AgentSessions));
        Assert.Equal(
            [HostAgentKinds.Dsharp, AgentKinds.Codex, AgentKinds.ClaudeCode, AgentKinds.OpenCode, AgentKinds.Terminal, AgentKinds.Acp],
            catalog.Select(entry => entry.AgentKind));
        Assert.Equal(HostSessionKinds.Harness, catalog[0].SessionKind);
        Assert.All(catalog.Where(entry => entry.AgentKind is AgentKinds.Terminal or AgentKinds.Acp), entry => Assert.True(entry.RequiresCommand));
        var openCode = Assert.Single(catalog, entry => entry.AgentKind == AgentKinds.OpenCode);
        Assert.Equal(AgentExecutableLocator.Resolve("opencode") is not null, openCode.SupportsAcp);
    }

    [Fact(Timeout = 90000)]
    public async Task UnknownTerminalAgent_WaitsForInput_RunsAfterInput_AndReportsItsExitCode()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell script as the agent");
        await using var host = await HarnessHostFixture.CreateAsync("mat", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("terminal", Ct);
        var changes = client.HostSessionChangesAsync(Ct);
        var added = client.NotificationsAddedAsync(Ct);
        var script = new RemoteAgentCommand("/bin/sh", ["-c", """printf 'Proceed? [y/N] '; read answer; echo "working on $answer"; sleep 3; exit 3"""]);

        var session = await client.StartAgentAsync(new RemoteAgentStartRequest(
            AgentKinds.Terminal, _workspace, Command: script, Title: "unknown agent", ScreenRules: new RemoteScreenRules(Idle: [@"\[y/N\]\s*$"])), Ct);
        var waiting = await RemoteHostMultiClientTests.NextAsync(changes, item => item.SessionId == session.SessionId && item.Status == HostSessionStatuses.WaitingForUser);
        var asked = await RemoteHostMultiClientTests.NextAsync(added, item => item.SessionId == session.SessionId);
        await Assert.ThrowsAsync<JsonRpcException>(() => client.PromptAgentAsync(session.SessionId, "y", Ct));
        await using var terminal = RemoteHostPtyTests.TerminalCapture.Attach(client, session.SessionId);
        await terminal.SendAsync("y\r");
        await terminal.WaitForAsync("working on y", TimeSpan.FromSeconds(10));
        var running = await RemoteHostMultiClientTests.NextAsync(changes, item => item.SessionId == session.SessionId && item.Status == HostSessionStatuses.Running);
        var exited = await RemoteHostMultiClientTests.NextAsync(changes, item => item.SessionId == session.SessionId && item.Status == HostSessionStatuses.Exited);
        var exitNotice = await RemoteHostMultiClientTests.NextAsync(added, item => item.SessionId == session.SessionId && item.Kind == NotificationKind.SessionExited);

        Assert.Equal((HostSessionKinds.External, AgentKinds.Terminal, _workspace), (session.Kind, session.AgentKind, session.ProjectPath));
        Assert.Equal("unknown agent", waiting.Title);
        Assert.Equal((NotificationKind.WaitingForInput, "unknown agent is waiting for input"), (asked.Kind, asked.Title));
        Assert.Null(running.ExitCode);
        Assert.Equal(3, exited.ExitCode);
        Assert.Equal("exit code 3", exitNotice.Body);
    }

    [Fact(Timeout = 60000)]
    public async Task HarnessSession_StartsThroughAgentStart_InANewOrAnExistingWorktree()
    {
        using var repo = new GitTestRepository();
        repo.Commit(("a.txt", "one\n"));
        await using var host = await HarnessHostFixture.CreateAsync("mah", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("harness", Ct);
        await HarnessHostFixture.OpenProjectAsync(client, repo.Sandbox, Ct);
        var worktree = new RemoteWorktreeChoice("../harness-wt", NewBranch: "harness-task");
        var request = new RemoteAgentStartRequest(HostAgentKinds.Dsharp, repo.Root, Worktree: worktree,
            Selection: RemoteHostMultiClientTests.Selection, Provider: RemoteHostMultiClientTests.Spec);

        var first = await client.StartAgentAsync(request, Ct);
        var second = await client.StartAgentAsync(request, Ct);
        var worktrees = await client.ListWorktreesAsync(repo.Root, Ct);

        var expected = Path.Combine(repo.Sandbox, "harness-wt");
        Assert.Equal((HostSessionKinds.Harness, HostAgentKinds.Dsharp, expected), (first.Kind, first.AgentKind, first.ProjectPath));
        Assert.Equal(expected, second.ProjectPath);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(("harness-task", 2), (Assert.Single(worktrees, item => item.Path == expected).Branch, worktrees.Count));
    }

    [Theory(Timeout = 120000)]
    [InlineData(AgentKinds.Codex)]
    [InlineData(AgentKinds.ClaudeCode)]
    public async Task BuiltInTerminalAgent_ReachesItsFirstScreen_AndStopsWithoutAPrompt(string agentKind)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the PTY agents run on POSIX hosts in these tests");
        await using var host = await HarnessHostFixture.CreateAsync("mab", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("builtin", Ct);
        if (!(await client.AgentCatalogAsync(Ct)).Single(entry => entry.AgentKind == agentKind).SupportsTerminal)
            Assert.Skip($"{agentKind} is not installed on this machine");
        var changes = client.HostSessionChangesAsync(Ct);

        var session = await client.StartAgentAsync(new RemoteAgentStartRequest(agentKind, _workspace, Columns: 120, Rows: 40), Ct);
        var waiting = await RemoteHostMultiClientTests.NextAsync(changes, item => item.SessionId == session.SessionId && item.Status == HostSessionStatuses.WaitingForUser, FirstScreen);
        await client.StopAgentAsync(session.SessionId, Ct);
        var exited = await RemoteHostMultiClientTests.NextAsync(changes, item => item.SessionId == session.SessionId && item.Status == HostSessionStatuses.Exited);

        Assert.Equal((HostSessionKinds.External, agentKind), (session.Kind, session.AgentKind));
        Assert.Equal(_workspace, waiting.ProjectPath);
        Assert.Equal(HostSessionStatuses.Exited, exited.Status);
    }

    [Fact(Timeout = 300000)]
    public async Task OpenCodeAcp_StopsAndResumesTheSameSession_AlsoAfterAHostRestart()
    {
        RequireOpenCode();
        await using var host = await HarnessHostFixture.CreateAsync("maa", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("acp", Ct);
        var changes = client.HostSessionChangesAsync(Ct);

        var session = await client.StartAgentAsync(new RemoteAgentStartRequest(AgentKinds.OpenCode, _workspace, Mode: RemoteAgentModes.Acp, Title: "opencode acp"), Ct);
        await client.StopAgentAsync(session.SessionId, Ct);
        var stopped = await RemoteHostMultiClientTests.NextAsync(changes, item => item.SessionId == session.SessionId && item.Status == HostSessionStatuses.Exited);
        var resumed = await client.ResumeAgentAsync(session.SessionId, Ct);
        await host.RestartHostAsync(Ct);
        var after = await host.ConnectAsync("after", Ct);
        var restored = Assert.Single(await after.ListHostSessionsAsync(Ct), item => item.SessionId == session.SessionId);
        var reopened = await after.ResumeAgentAsync(session.SessionId, Ct);
        await after.StopAgentAsync(session.SessionId, Ct);

        Assert.Equal((HostSessionKinds.Acp, AgentKinds.OpenCode, HostSessionStatuses.Idle), (session.Kind, session.AgentKind, session.Status));
        Assert.Equal(HostSessionStatuses.Exited, stopped.Status);
        Assert.Equal((session.SessionId, HostSessionStatuses.Idle), (resumed.SessionId, resumed.Status));
        Assert.Equal(HostSessionStatuses.Exited, restored.Status);
        Assert.Equal((session.SessionId, HostSessionStatuses.Idle, "opencode acp"), (reopened.SessionId, reopened.Status, reopened.Title));
    }

    [Fact(Timeout = 360000)]
    [Trait("Category", "LlmSmoke")]
    public async Task OpenCodeAcp_PromptTurn_BecomesHarnessSessionEvents()
    {
        RequireOpenCode();
        await using var host = await HarnessHostFixture.CreateAsync("map", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("acp", Ct);
        var session = await client.StartAgentAsync(new RemoteAgentStartRequest(AgentKinds.OpenCode, _workspace, Mode: RemoteAgentModes.Acp), Ct);

        await client.PromptAgentAsync(session.SessionId, "Reply with exactly the word pong and nothing else. Do not use any tools.", Ct);
        var events = await TurnEventsAsync(client, session.SessionId);
        var idle = await RemoteHostMultiClientTests.NextAsync(client.HostSessionChangesAsync(Ct), item => item.SessionId == session.SessionId && item.Status == HostSessionStatuses.Idle);

        Assert.IsType<TurnStartPayload>(events[0].Data);
        Assert.IsType<UserMessagePayload>(events[1].Data);
        Assert.Equal(Enumerable.Range(0, events.Count).Select(seq => (long)seq), events.Select(item => item.Seq));
        Assert.Contains(events, item => item.Data is AssistantChunkPayload);
        var reply = string.Concat(events.Select(item => item.Data).OfType<AssistantMessagePayload>().SelectMany(payload => payload.Message.Content).OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains("pong", reply, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<TurnEndReason.Completed>(Assert.IsType<TurnEndPayload>(events[^1].Data).Reason);
        Assert.Equal(HostSessionStatuses.Idle, idle.Status);
    }

    [Fact(Timeout = 360000)]
    [Trait("Category", "LlmSmoke")]
    public async Task OpenCodeAcp_PermissionRequest_ReachesEveryClient_AndTheFirstAnswerWins()
    {
        RequireOpenCode();
        await using var host = await HarnessHostFixture.CreateAsync("mar", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        // Inline OpenCode config for the agent process only: shell commands ask for permission instead of running.
        var askForShell = new Dictionary<string, string> { ["OPENCODE_CONFIG_CONTENT"] = """{"permission":{"bash":"ask","edit":"ask"}}""" };
        var session = await alice.StartAgentAsync(new RemoteAgentStartRequest(AgentKinds.OpenCode, _workspace, Mode: RemoteAgentModes.Acp, Environment: askForShell), Ct);

        await alice.PromptAgentAsync(session.SessionId, "Run exactly this shell command once: echo dsh-acp-probe > acp-probe.txt", Ct);
        var request = await RemoteHostMultiClientTests.NextAsync(alice.ApprovalsAsync(Ct), within: Turn);
        var seenByBob = await RemoteHostMultiClientTests.NextAsync(bob.ApprovalsAsync(Ct));
        var allowed = await alice.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct);
        var late = await bob.RespondApprovalAsync(request.RequestId, false, cancellationToken: Ct);
        var events = await TurnEventsAsync(bob, session.SessionId);

        Assert.Equal(request, seenByBob);
        Assert.Equal(session.SessionId, request.SessionId);
        Assert.Equal((true, false), (allowed, late));
        Assert.Contains(events, item => item.Data is ToolCallPayload);
        Assert.Contains(events, item => item.Data is ToolResultPayload { Error: null });
        Assert.Equal("dsh-acp-probe", (await File.ReadAllTextAsync(Path.Combine(_workspace, "acp-probe.txt"), Ct)).Trim());
    }

    private static void RequireOpenCode()
    {
        if (AgentExecutableLocator.Resolve("opencode") is null)
            Assert.Skip("opencode is not installed on this machine");
    }

    /** The session's events from the start up to the end of its first turn. */
    private static async Task<List<SessionEvent>> TurnEventsAsync(RemoteHostClient client, string sessionId)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Turn + Handshake);
        var events = new List<SessionEvent>();
        await foreach (var item in client.SubscribeAsync(sessionId, 0, timeout.Token))
        {
            events.Add(DshJson.Deserialize<SessionEvent>(item.EventJson)!);
            if (events[^1].Data is TurnEndPayload)
                return events;
        }
        throw new InvalidOperationException("the event stream ended before the turn did");
    }
}
