using Avalonia.Threading;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** Ordinary Chat uses the same authenticated Host transport as other clients, not a GUI AgentRegistry. */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class GuiHostChatIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HarnessModelChoice Model = new(new RemoteModelSelection("test", "test-model"), null);

    [Fact(Timeout = 120000)]
    public async Task OrdinaryChat_CreatesOnlyOnHost_AndBroadcastsComposerImagesAndCommands()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gchat", TestContext.Current.CancellationToken);
        using var gui = await GuiTestEnvironment.CreateAsync();
        var observer = await host.ConnectAsync("other-client", Ct);
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, null, Model, Ct);
            var agents = gui.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var before = agents.List().Count;
            using var viewModel = new MainViewModel(gui.App, chat);
            var id = viewModel.CurrentSessionId.Value;
            Assert.Equal(before, agents.List().Count);
            Assert.Contains(await observer.ListHostSessionsAsync(Ct), session => session.SessionId == id && session.Kind == HostSessionKinds.Harness);

            viewModel.Composer.Input = "/preset creative";
            await GuiTestEnvironment.PumpAsync(viewModel.SubmitCommand.ExecuteAsync(null));
            Assert.Equal("creative", viewModel.Preset);
            var snapshot = await observer.GetSessionSnapshotAsync(id, Ct);
            Assert.Contains(snapshot.Events, item => DshJson.Deserialize<SessionEvent>(item.EventJson)?.Type == "preset/mode");

            var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3 };
            viewModel.AttachImage("clipboard.png", png, 3, 2);
            viewModel.Composer.Input = "ordinary local chat";
            await GuiTestEnvironment.PumpAsync(viewModel.SubmitCommand.ExecuteAsync(null));
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages,
                () => viewModel.Messages.Any(message => message.Kind == MessageKind.User && message.Text.Contains("ordinary local chat", StringComparison.Ordinal)));
            Assert.Single(viewModel.Messages, message => message.Kind == MessageKind.User && message.Text.Contains("ordinary local chat", StringComparison.Ordinal));
            Assert.Empty(viewModel.Attachments);
            snapshot = await observer.GetSessionSnapshotAsync(id, Ct);
            var user = Assert.Single(snapshot.Events.Select(item => DshJson.Deserialize<SessionEvent>(item.EventJson)?.Data)
                .OfType<UserMessagePayload>(), item => item.Message.Content.OfType<TextBlock>().Any(text => text.Text == "ordinary local chat"));
            Assert.Contains(user.Message.Content, block => block is ImageBlock { Attachment.Width: 3, Attachment.Height: 2 });
            Assert.Equal(before, agents.List().Count);
        });
    }

    [Fact(Timeout = 120000)]
    public async Task ClosingGui_DetachesOnly_AndPendingTaskCanBeAnsweredByAnotherClient()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gclose", TestContext.Current.CancellationToken);
        using var gui = await GuiTestEnvironment.CreateAsync();
        var observer = await host.ConnectAsync("survivor", Ct);
        string id = "";
        Task<ApprovalOutcome>? waiting = null;
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, null, Model, Ct);
            using (var viewModel = new MainViewModel(gui.App, chat))
            {
                id = viewModel.CurrentSessionId.Value;
                var agent = (AgentLoopAgent)host.Environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(SessionId.Create(id))!;
                waiting = RemoteHostMultiClientTests.RaiseApprovalAsync(host, agent, "bash");
                await GuiTestEnvironment.WaitForAsync(viewModel.Workspace.Notifications.Pending,
                    () => viewModel.Workspace.Notifications.Pending.Any(item => item.SessionId == id));
            }
            Dispatcher.UIThread.RunJobs();
        });
        Assert.NotNull(waiting);
        Assert.False(waiting.IsCompleted);
        var request = Assert.Single((await observer.ListPendingAsync(Ct)).Approvals, item => item.SessionId == id);
        Assert.True(await observer.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct));
        Assert.Equal(ApprovalOutcome.AllowedOnce, await waiting.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.Contains(await observer.ListHostSessionsAsync(Ct), session => session.SessionId == id && session.Status != HostSessionStatuses.Exited);
        await observer.StopAgentAsync(id, Ct);
        Assert.Contains(await observer.ListHostSessionsAsync(Ct), session => session.SessionId == id && session.Status == HostSessionStatuses.Exited);
    }

    [Fact(Timeout = 120000)]
    public async Task PersistedPreFleetChat_IsResumedByHostWithSameIdentityAndFullTranscript()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gmigrate", TestContext.Current.CancellationToken);
        var legacy = host.Environment.Agent.Session;
        var id = legacy.Id.Value;
        legacy.Append(new UserMessagePayload(MessageFactory.CreateUserText("persisted before fleet")), new SurfaceOp.Append());
        await host.Environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(legacy);
        await host.RestartHostAsync(Ct);
        using var gui = await GuiTestEnvironment.CreateAsync();
        var observer = await host.ConnectAsync("migration-observer", Ct);
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, id, Model, Ct);
            using var viewModel = new MainViewModel(gui.App, chat);
            Assert.Equal(id, viewModel.CurrentSessionId.Value);
            Assert.Contains(viewModel.Messages, message => message.Kind == MessageKind.User && message.Text == "persisted before fleet");
            Assert.Contains(await observer.ListHostSessionsAsync(Ct), session => session.SessionId == id);
            Assert.DoesNotContain(gui.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.List(), agent => agent.Id.Value == id);
        });
    }

    [Fact(Timeout = 120000)]
    public async Task HostTruncation_RebuildsRichChatProjection_WithoutKeepingDiscardedMessages()
    {
        await using var host = await HarnessHostFixture.CreateAsync("greset", TestContext.Current.CancellationToken);
        using var gui = await GuiTestEnvironment.CreateAsync();
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, null, Model, Ct);
            using var viewModel = new MainViewModel(gui.App, chat);
            var agent = host.Environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(viewModel.CurrentSessionId)!;
            var keepCount = agent.Session.Seq;
            agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("discarded tail")), new SurfaceOp.Append());
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.Any(message => message.Text == "discarded tail"));
            Assert.True(agent.Session.TryTruncateTo(keepCount));
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.All(message => message.Text != "discarded tail"));
            agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("replacement at reused seq")), new SurfaceOp.Append());
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.Any(message => message.Text == "replacement at reused seq"));
            Assert.Single(viewModel.Messages, message => message.Text == "replacement at reused seq");
            Assert.DoesNotContain(viewModel.Messages, message => message.Text == "discarded tail");
        });
    }

    [Fact(Timeout = 120000)]
    public async Task AnotherClientAnswer_ClosesOrdinaryChatDecisionDialog_AndWinsOnce()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gdecision", TestContext.Current.CancellationToken);
        using var gui = await GuiTestEnvironment.CreateAsync();
        var observer = await host.ConnectAsync("answering-client", Ct);
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, null, Model, Ct);
            using var viewModel = new MainViewModel(gui.App, chat);
            var shown = new TaskCompletionSource<DecisionViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.DecisionRequested += decision =>
            {
                decision.Closed += value => closed.TrySetResult(value.Result);
                shown.TrySetResult(decision);
                return closed.Task;
            };
            var agent = host.Environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(viewModel.CurrentSessionId)!;
            var waiting = RemoteHostMultiClientTests.RaiseApprovalAsync(host, agent, "bash");
            var dialog = await GuiTestEnvironment.PumpAsync(shown.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            Assert.True(dialog.IsApproval);
            var request = Assert.Single((await observer.ListPendingAsync(Ct)).Approvals, item => item.SessionId == viewModel.CurrentSessionId.Value);
            Assert.True(await observer.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct));
            Assert.Null(await GuiTestEnvironment.PumpAsync(closed.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct)));
            Assert.Equal(ApprovalOutcome.AllowedOnce, await waiting.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            Assert.False(await observer.RespondApprovalAsync(request.RequestId, false, cancellationToken: Ct));
        });
    }

    private static FleetWorkspace Fleet(HarnessHostFixture host, GuiTestEnvironment gui)
    {
        var options = FleetConnectionTests.TestOptions with { ClientName = "ordinary-gui-chat" };
        return new FleetWorkspace(new MachineStore(gui.App.Home), options,
            () => new FleetClient([MachineSpec.Local(host.Root)], options));
    }
}
