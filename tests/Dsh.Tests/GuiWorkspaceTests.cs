using Avalonia.Media;
using Avalonia.Threading;
using Dsh.AgentAdapters;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Notifications;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * The GUI workspace (dashboard, quick switcher, notification center, session views) against two real hosts: two independent harness homes act as two machines.
 * Sessions are real: terminal sessions run real shell commands, harness sessions are real agents whose events are appended without calling a model.
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class GuiWorkspaceTests
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(20);

    /** Fast heartbeat and backoff, so a stopped host is noticed and the reconnect happens within the test. */
    private static readonly FleetOptions Options = FleetConnectionTests.TestOptions with { ClientName = "gui-workspace-test" };

    /** Prints a prompt and waits; the screen rule turns the session into WaitingForUser while the prompt is the last line. */
    private static readonly RemoteScreenRules WaitingRules = new(Idle: [@"\[y/N\]\s*$"]);

    private const string WaitingScript = "printf 'Deploy now? [y/N] '; read answer; echo \"deploying $answer\"; sleep 30";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 120000)]
    public async Task Dashboard_GroupsBothMachines_FiltersSearchesSorts_AndPaletteJumps()
    {
        var ct = TestContext.Current.CancellationToken;
        SkipOnWindows();
        await using var alpha = await HarnessHostFixture.CreateAsync("gwa", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("gwb", ct);
        var appProject = Project(alpha, "app");
        var serviceProject = Project(beta, "service");
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            var destinations = new List<WorkspaceDestination>();
            workspace.NavigationRequested += destinations.Add;
            try
            {
                await ConnectedAsync(workspace);
                var waiting = await StartTerminalAsync(workspace, "alpha", appProject, "deploy 等待确认", WaitingScript, WaitingRules);
                var busy = await StartTerminalAsync(workspace, "alpha", appProject, "build 长任务", "sleep 30");
                var done = await StartTerminalAsync(workspace, "beta", serviceProject, "构建完成", "exit 0");
                var dashboard = workspace.Dashboard;
                await UntilAsync(() => Card(workspace, "alpha", waiting)?.NeedsAttention == true
                    && Card(workspace, "alpha", busy)?.IsRunning == true
                    && Card(workspace, "beta", done)?.IsExited == true, "session states");

                Assert.Equal(["alpha", "beta"], dashboard.Machines.Select(machine => machine.Name));
                Assert.All(dashboard.Machines, machine => Assert.True(machine.IsConnected && machine.VersionLabel.Length > 0));
                var alphaProjects = Assert.Single(dashboard.Machines[0].Projects);
                Assert.Equal(appProject, alphaProjects.ProjectPath);
                Assert.Equal(new[] { waiting, busy }.Order(), alphaProjects.Sessions.Select(card => card.SessionId).Order());
                Assert.Equal(serviceProject, Assert.Single(dashboard.Machines[1].Projects).ProjectPath);
                Assert.Equal((3, 1, 1, 0), (dashboard.SessionCount, dashboard.AttentionCount, dashboard.RunningCount, dashboard.OfflineMachineCount));
                Assert.Equal("等待你处理", Card(workspace, "alpha", waiting)!.StatusLabel);

                dashboard.ShowAttentionCommand.Execute(null);
                Assert.Equal([waiting], VisibleSessions(dashboard));
                dashboard.Filter = DashboardFilter.Exited;
                Assert.Equal([done], VisibleSessions(dashboard));
                dashboard.Filter = DashboardFilter.All;
                dashboard.SearchText = "长任务";
                Assert.Equal([busy], VisibleSessions(dashboard));
                dashboard.SearchText = "";
                dashboard.Sort = DashboardSort.Status;
                Assert.Equal([waiting, busy], alphaProjects.Sessions.Select(card => card.SessionId));
                dashboard.Sort = DashboardSort.Title;
                Assert.Equal([busy, waiting], alphaProjects.Sessions.Select(card => card.SessionId));

                var palette = workspace.Palette;
                palette.ToggleCommand.Execute(null);
                Assert.Equal(waiting, palette.Results[0].SessionId);
                palette.Query = "beta 构建";
                Assert.Equal(done, Assert.Single(palette.Results).SessionId);
                palette.Query = "chrw";
                Assert.Empty(palette.Results);
                palette.Query = "长务";
                palette.ConfirmCommand.Execute(null);
                Assert.False(palette.IsOpen);
                Assert.Equal(busy, workspace.ActiveSession?.SessionId);
                Assert.Equal(WorkspaceDestination.Session, destinations[^1]);

                workspace.ShowDashboard();
                Assert.Null(workspace.ActiveSession);
                Assert.Equal(DashboardSort.Title, dashboard.Sort);
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    [Fact(Timeout = 120000)]
    public async Task NotificationCenter_SyncsReadMarks_AnswersPendingOnce_AndNotifiesDesktop()
    {
        var ct = TestContext.Current.CancellationToken;
        SkipOnWindows();
        await using var alpha = await HarnessHostFixture.CreateAsync("gna", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("gnb", ct);
        var betaProject = Project(beta, "web");
        var bob = await alpha.ConnectAsync("bob", ct);
        var carol = await beta.ConnectAsync("carol", ct);
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            var notifications = workspace.Notifications;
            var desktop = new List<string>();
            var toasts = new List<string>();
            notifications.IsForeground = () => false;
            notifications.DesktopNotify = (title, _) => desktop.Add(title);
            notifications.Toast = (title, _, _) => toasts.Add(title);
            try
            {
                await ConnectedAsync(workspace);
                var approval = RemoteHostMultiClientTests.RaiseApprovalAsync(alpha, alpha.Environment.Agent, "bash");
                var waiting = await StartTerminalAsync(workspace, "beta", betaProject, "等待输入", WaitingScript, WaitingRules);
                await UntilAsync(() => notifications.Pending.Count == 1
                    && notifications.Items.Any(item => item is { Machine: "alpha", Kind: NotificationKind.ApprovalRequest })
                    && notifications.Items.Any(item => item is { Machine: "beta", Kind: NotificationKind.WaitingForInput }), "approval and waiting notifications");
                Assert.True(notifications.UnreadTotal >= 2);
                Assert.Contains(desktop, title => title.StartsWith("alpha · ", StringComparison.Ordinal));
                Assert.Contains(desktop, title => title.StartsWith("beta · ", StringComparison.Ordinal));
                Assert.Equal(notifications.Items.OrderByDescending(item => item.CreatedAt), notifications.Items);

                notifications.SelectedMachine = notifications.MachineOptions.Single(option => option.Value == "beta");
                Assert.All(notifications.Items, item => Assert.Equal("beta", item.Machine));
                notifications.SelectedMachine = notifications.MachineOptions[0];
                notifications.SelectedKind = notifications.KindOptions.Single(option => option.Value == nameof(NotificationKind.ApprovalRequest));
                Assert.All(notifications.Items, item => Assert.Equal(NotificationKind.ApprovalRequest, item.Kind));
                notifications.SelectedKind = notifications.KindOptions[0];

                // Another client of alpha marks everything read: this client's marks and counts follow.
                var alphaUnread = workspace.Fleet.Find("alpha")!.UnreadNotifications;
                Assert.True(alphaUnread > 0);
                await bob.MarkNotificationsReadAsync(new RemoteNotifyMarkRead(All: true), ct);
                await UntilAsync(() => workspace.Fleet.Find("alpha")!.UnreadNotifications == 0
                    && notifications.Items.Where(item => item.Machine == "alpha").All(item => item.IsRead), "read marks from another client");

                // Marking one read here reaches the host (and every other client).
                var betaItem = notifications.Items.First(item => item is { Machine: "beta", IsUnread: true });
                await notifications.MarkReadCommand.ExecuteAsync(betaItem);
                var betaPage = await carol.ListNotificationsAsync(new RemoteNotifyQuery(Limit: NotificationCenterViewModel.HistoryLimit), ct);
                Assert.True(betaPage.Items.Single(record => record.Id == betaItem.Id).IsRead);

                // The first answer wins: bob answers, the request leaves this client's list, and answering it here is refused.
                var pending = Assert.Single(notifications.Pending);
                Assert.Equal(("alpha", "bash"), (pending.Machine, pending.Item.Approval!.Tool));
                var seen = Assert.Single((await bob.ListPendingAsync(ct)).Approvals);
                Assert.True(await bob.RespondApprovalAsync(seen.RequestId, true, cancellationToken: ct));
                Assert.Equal(ApprovalOutcome.AllowedOnce, await approval.WaitAsync(Arrival, ct));
                await UntilAsync(() => notifications.Pending.Count == 0, "resolution removes the request");
                await pending.AllowCommand.ExecuteAsync(null);
                Assert.Contains("请求已被处理", toasts);

                // Opening a notification jumps to its session.
                var waitingItem = notifications.Items.First(item => item.SessionId == waiting);
                await notifications.OpenCommand.ExecuteAsync(waitingItem);
                Assert.Equal(waiting, workspace.ActiveSession?.SessionId);
                Assert.True(waitingItem.IsRead);
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    [Fact(Timeout = 120000)]
    public async Task PendingApproval_AnsweredFromTheSessionHeader_ReachesTheHost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alpha = await HarnessHostFixture.CreateAsync("gpa", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("gpb", ct);
        var project = Project(alpha, "repo");
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            try
            {
                await ConnectedAsync(workspace);
                var started = await workspace.Actions.StartAsync("alpha", Harness(project, "审批会话"), ct);
                var agent = RemoteHostMultiClientTests.Agent(alpha, started.SessionId);
                var approval = RemoteHostMultiClientTests.RaiseApprovalAsync(alpha, agent, "write");
                await UntilAsync(() => Card(workspace, "alpha", started.SessionId) is { HasPending: true, NeedsAttention: true }, "pending count on the card");
                workspace.OpenSession("alpha", started.SessionId);
                var session = workspace.ActiveSession!;
                await UntilAsync(() => session.HasPending, "pending request in the session header");

                await Assert.Single(session.Pending).RejectCommand.ExecuteAsync(null);

                Assert.Equal(ApprovalOutcome.Rejected, await approval.WaitAsync(Arrival, ct));
                await UntilAsync(() => !session.HasPending && workspace.Notifications.Pending.Count == 0, "the answered request disappears");
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    [Fact(Timeout = 120000)]
    public async Task SessionView_AttachesToTerminal_SharesIt_AndOpensAShellInTheProject()
    {
        var ct = TestContext.Current.CancellationToken;
        SkipOnWindows();
        await using var alpha = await HarnessHostFixture.CreateAsync("gta", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("gtb", ct);
        var project = Project(alpha, "tool");
        var other = await alpha.ConnectAsync("other-client", ct);
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            try
            {
                await ConnectedAsync(workspace);
                var echo = await StartTerminalAsync(workspace, "alpha", project, "回显", "echo ready; read line; echo \"got:$line\"; sleep 30");
                workspace.OpenSession("alpha", echo);
                var session = workspace.ActiveSession!;
                var terminal = Assert.Single(session.Tabs).Terminal!;
                await UntilAsync(() => ScreenText(terminal).Contains("ready", StringComparison.Ordinal), "the session's output");

                await using var shared = RemoteHostPtyTests.TerminalCapture.Attach(other, echo);
                terminal.Session!.Send("hello\r"u8);
                await UntilAsync(() => ScreenText(terminal).Contains("got:hello", StringComparison.Ordinal), "the reply to typed input");
                await shared.WaitForAsync("got:hello", Arrival);

                await session.AddShellCommand.ExecuteAsync(null);
                var shellTab = session.Tabs[^1];
                Assert.True(shellTab is { IsClosable: true, IsSelected: true });
                var shellSessions = workspace.Fleet.Find("alpha")!.Sessions.Where(item => item.Kind == HostSessionKinds.Terminal).ToList();
                shellTab.Terminal!.Session!.Send("pwd\r"u8);
                await UntilAsync(() => ScreenText(shellTab.Terminal).Contains(project, StringComparison.Ordinal), "the shell runs in the project directory");

                var shellId = Assert.Single(shellSessions).SessionId;
                await session.CloseTabCommand.ExecuteAsync(shellTab);
                Assert.Single(session.Tabs);
                await UntilAsync(() => workspace.Fleet.Find("alpha")!.Sessions.Single(item => item.SessionId == shellId).Status == HostSessionStatuses.Exited, "closing the tab ends its shell");
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    [Fact(Timeout = 120000)]
    public async Task HarnessTranscript_RendersHostEvents_AndContinuesAfterAHostRestartWithoutDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alpha = await HarnessHostFixture.CreateAsync("gha", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("ghb", ct);
        var project = Project(beta, "notes");
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            try
            {
                await ConnectedAsync(workspace);
                var started = await workspace.Actions.StartAsync("beta", Harness(project, "整理笔记"), ct);
                var agentSession = RemoteHostMultiClientTests.Agent(beta, started.SessionId).Session;
                agentSession.Append(new UserMessagePayload(MessageFactory.CreateUserText("列出计划")), new SurfaceOp.Append());
                agentSession.Append(new SystemMessagePayload(1, 0, MessageFactory.CreateSystemMessage([new TextBlock("1. 读笔记\n2. 归类")], new PluginMessageSource("acp-plan"))), new SurfaceOp.Append());
                await UntilAsync(() => Card(workspace, "beta", started.SessionId) is not null, "the harness session's card");
                workspace.OpenSession("beta", started.SessionId);
                var transcript = Assert.Single(workspace.ActiveSession!.Tabs).Transcript!;
                await UntilAsync(() => transcript.Messages.Count == 2, "the session's history");
                Assert.Equal(("你", "列出计划"), (transcript.Messages[0].Role, transcript.Messages[0].Text));
                Assert.Equal(("计划", MessageKind.System), (transcript.Messages[1].Role, transcript.Messages[1].Kind));

                // The host restarts: its agents end, the machine is shown reconnecting, and the session comes back exited with its history.
                await beta.StopHostAsync();
                var card = Card(workspace, "beta", started.SessionId)!;
                await UntilAsync(() => !card.IsMachineOnline && workspace.Dashboard.OfflineMachineCount == 1, "the machine is shown unreachable");
                Assert.Equal("重连中", card.StatusLabel);
                await beta.StartHostAsync(ct);
                await UntilAsync(() => workspace.Dashboard.OfflineMachineCount == 0 && card.IsExited, "the machine is back and the session exited");

                // Resuming the session continues the same transcript: new events arrive, the history is not repeated.
                await workspace.Actions.ResumeAsync("beta", card.Session, ct);
                var resumed = RemoteHostMultiClientTests.Agent(beta, started.SessionId).Session;
                resumed.Append(new AssistantMessagePayload(1, 0, MessageFactory.CreateAssistantMessage([new TextBlock("已归类 3 条")], "client-provider", "client-model")), new SurfaceOp.Append());
                await UntilAsync(() => transcript.Messages.Count == 3, "the resumed session's new message");
                Assert.Equal("已归类 3 条", transcript.Messages[2].Text);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(["列出计划", "1. 读笔记\n2. 归类", "已归类 3 条"], transcript.Messages.Select(message => message.Text));
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    /** The test page is a plain TextBlock: it stands in for a module's page and only checks the navigation contract. */
    [Fact(Timeout = 60000)]
    public async Task WorkspacePages_ShowNothingUntilRegistered_ThenOpenWithTheWorkspaceContext()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alpha = await HarnessHostFixture.CreateAsync("gxa", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("gxb", ct);
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            var destinations = new List<WorkspaceDestination>();
            workspace.NavigationRequested += destinations.Add;
            try
            {
                Assert.Empty(workspace.Pages.Available);
                WorkspacePageContext? opened = null;
                var page = new WorkspacePageEntry(WorkspacePages.Git, "Git", Geometry.Parse("M0,0 L1,1"), context =>
                {
                    opened = context;
                    return new Avalonia.Controls.TextBlock { Text = "git page" };
                });
                workspace.Pages.Register(page);
                workspace.Pages.Register(page);
                Assert.Same(page, Assert.Single(workspace.Pages.Available));

                workspace.OpenPage(page);

                Assert.Equal((null, null), (opened?.Machine, opened?.ProjectPath));
                Assert.Same(workspace.Fleet, opened?.Fleet);
                Assert.Equal("git page", Assert.IsType<Avalonia.Controls.TextBlock>(workspace.PageContent).Text);
                Assert.Equal((page, WorkspaceDestination.Page), (workspace.ActivePage, destinations[^1]));
                workspace.ShowDashboard();
                Assert.Equal((null, null), (workspace.PageContent, workspace.ActivePage));
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    [Fact]
    public void ParseCommand_SplitsWords_AndKeepsQuotedSpaces()
    {
        var command = NewSessionViewModel.ParseCommand("""  my-agent --name "two words" 'it''s' a\ b {prompt}  """);
        Assert.Equal("my-agent", command.Executable);
        Assert.Equal(["--name", "two words", "its", "a b", "{prompt}"], command.Arguments);
        Assert.Throws<ArgumentException>(() => NewSessionViewModel.ParseCommand("   "));
    }

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("terminal sessions run POSIX shell commands");
    }

    /** The workspace over both hosts, with the machines named as the dashboard shows them. */
    private static WorkspaceViewModel Open(HarnessHostFixture alpha, HarnessHostFixture beta)
    {
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(alpha.Root, "host.sock"));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(beta.Root, "host.sock"));
        var fleet = new FleetWorkspace(() => new FleetClient([MachineSpec.Local(alpha.Root, "alpha"), MachineSpec.Local(beta.Root, "beta")], Options));
        var workspace = new WorkspaceViewModel(fleet, () => new HarnessModelChoice(RemoteHostMultiClientTests.Selection, RemoteHostMultiClientTests.Spec), (_, _, _) => { });
        workspace.Start();
        return workspace;
    }

    private static async Task ConnectedAsync(WorkspaceViewModel workspace)
    {
        foreach (var machine in workspace.Fleet.Client.Machines)
            await machine.WaitConnectedAsync(Ct);
        await UntilAsync(() => workspace.Dashboard.Machines.All(machine => machine.IsConnected), "both machines connected");
    }

    private static string Project(HarnessHostFixture host, string name)
        => Directory.CreateDirectory(Path.Combine(host.Root, name)).FullName;

    private static async Task<string> StartTerminalAsync(
        WorkspaceViewModel workspace, string machine, string project, string title, string script, RemoteScreenRules? rules = null)
    {
        var request = new RemoteAgentStartRequest(
            AgentKinds.Terminal, project, Command: new RemoteAgentCommand("/bin/sh", ["-c", script]), Title: title, ScreenRules: rules);
        return (await workspace.Actions.StartAsync(machine, request, Ct)).SessionId;
    }

    private static RemoteAgentStartRequest Harness(string project, string title) => new(HostAgentKinds.Dsharp, project, Title: title);

    private static SessionCardViewModel? Card(WorkspaceViewModel workspace, string machine, string sessionId)
        => workspace.Dashboard.Find(machine, sessionId);

    private static List<string> VisibleSessions(DashboardViewModel dashboard)
        => [.. dashboard.Machines.SelectMany(machine => machine.Projects).SelectMany(project => project.Sessions).Select(card => card.SessionId)];

    /** The visible screen of a terminal tab, row by row. */
    private static string ScreenText(TerminalViewModel? terminal)
    {
        if (terminal?.Session is not { } session)
            return "";
        lock (session.Gate)
        {
            var screen = session.Screen;
            var rows = new List<string>();
            for (var y = 0; y < screen.Height; y++)
            {
                var row = screen.Row(y);
                var characters = new char[row.Length];
                for (var x = 0; x < row.Length; x++)
                    characters[x] = row[x].Character;
                rows.Add(new string(characters).TrimEnd());
            }
            return string.Join('\n', rows);
        }
    }

    /** Polls on the UI thread, running the posted UI work between checks. */
    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Arrival;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out waiting for {what}");
            await Task.Delay(50, Ct);
        }
    }
}
