using Dsh.Core;
using Dsh.Host;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 多客户端连同一个真实 host(harness 后端 + loopback 监听): 审批/问答广播与先答者生效、断线后仍可被别的客户端回答、
 * host.pending.list、会话登记表推送与重启恢复。审批经 harness 的审批瀑布直接发起(ApprovalService.Request 要求处在一轮对话之内, 而这里不调用 LLM)。
 */
public sealed class RemoteHostMultiClientTests
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60000)]
    public async Task Approval_IsBroadcast_FirstAnswerWins_AndResolutionReachesEveryone()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mha", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);

        var decision = RaiseApprovalAsync(host, host.Environment.Agent, "bash");
        var seenByAlice = await NextAsync(alice.ApprovalsAsync(Ct));
        var seenByBob = await NextAsync(bob.ApprovalsAsync(Ct));
        Assert.Equal(seenByAlice, seenByBob);
        Assert.Equal(host.Environment.Agent.Id.Value, seenByAlice.SessionId);
        Assert.Equal("bash", seenByAlice.Tool);

        Assert.True(await alice.RespondApprovalAsync(seenByAlice.RequestId, true, cancellationToken: Ct));
        Assert.False(await bob.RespondApprovalAsync(seenByBob.RequestId, false, cancellationToken: Ct));
        Assert.Equal(ApprovalOutcome.AllowedOnce, await decision.WaitAsync(Arrival, Ct));

        foreach (var client in new[] { alice, bob })
        {
            var resolved = await NextAsync(client.ResolutionsAsync(Ct));
            Assert.Equal(seenByAlice.RequestId, resolved.RequestId);
            Assert.Equal(PendingKinds.Approval, resolved.Kind);
            Assert.Equal(PendingResolutions.Allowed, resolved.Resolution);
            Assert.Equal("alice", resolved.ResolvedBy);
            Assert.Equal(alice.Hello.ConnectionId, resolved.ResolvedByConnection);
        }
        Assert.Empty((await bob.ListPendingAsync(Ct)).Approvals);
    }

    [Fact(Timeout = 60000)]
    public async Task PendingRequests_SurviveDisconnect_AndReachLateJoiners()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhd", TestContext.Current.CancellationToken);
        var first = await host.ConnectAsync("first", Ct);
        var approval = RaiseApprovalAsync(host, host.Environment.Agent, "write");
        var question = AskQuestionAsync(host, host.Environment.Agent);
        await NextAsync(first.ApprovalsAsync(Ct));
        await NextAsync(first.QuestionsAsync(Ct));
        await first.DisposeAsync();

        var late = await host.ConnectAsync("late", Ct);
        var pending = await late.ListPendingAsync(Ct);
        var pendingApproval = Assert.Single(pending.Approvals);
        var pendingQuestion = Assert.Single(pending.Questions);
        Assert.Equal(pendingApproval, await NextAsync(late.ApprovalsAsync(Ct)));
        Assert.Equal(pendingQuestion.RequestId, (await NextAsync(late.QuestionsAsync(Ct))).RequestId);

        Assert.True(await late.RespondApprovalAsync(pendingApproval.RequestId, false, cancellationToken: Ct));
        Assert.True(await late.RespondQuestionAsync(pendingQuestion.RequestId, [new RemoteQuestionAnswerItem("q1", ["B"], null)], Ct));
        Assert.Equal(ApprovalOutcome.Rejected, await approval.WaitAsync(Arrival, Ct));
        Assert.Equal(["B"], (await question.WaitAsync(Arrival, Ct)).Answers[0].Selected);
        var empty = await late.ListPendingAsync(Ct);
        Assert.Empty(empty.Approvals);
        Assert.Empty(empty.Questions);
    }

    [Fact(Timeout = 60000)]
    public async Task Interrupt_CancelsPendingRequests_AndBroadcastsCancellation()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhi", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("solo", Ct);
        var created = await client.CreateSessionAsync(host.Environment.App.Home.Root, Selection, Spec, Ct);
        var agent = Agent(host, created.Id);
        var approval = RaiseApprovalAsync(host, agent, "bash");
        await NextAsync(client.ApprovalsAsync(Ct));

        await client.InterruptAsync(created.Id, Ct);

        Assert.Equal(ApprovalOutcome.Cancelled, await approval.WaitAsync(Arrival, Ct));
        var resolved = await NextAsync(client.ResolutionsAsync(Ct));
        Assert.Equal(PendingResolutions.Cancelled, resolved.Resolution);
        Assert.Equal(PendingResolver.Host.Name, resolved.ResolvedBy);
        Assert.Null(resolved.ResolvedByConnection);
    }

    [Fact(Timeout = 60000)]
    public async Task SessionRegistry_PushesChanges_AndDerivesWaitingForUser()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhs", TestContext.Current.CancellationToken);
        var creator = await host.ConnectAsync("creator", Ct);
        var watcher = await host.ConnectAsync("watcher", Ct);
        var changes = watcher.HostSessionChangesAsync(Ct);
        var cwd = host.Environment.App.Home.Root;

        var created = await creator.CreateSessionAsync(cwd, Selection, Spec, Ct);
        var registered = await NextAsync(changes, session => session.SessionId == created.Id);
        Assert.Equal(HostSessionKinds.Harness, registered.Kind);
        Assert.Equal(HostAgentKinds.Dsharp, registered.AgentKind);
        Assert.Equal(cwd, registered.ProjectPath);
        Assert.Equal(HostSessionStatuses.Idle, registered.Status);

        var approval = RaiseApprovalAsync(host, Agent(host, created.Id), "bash");
        var waiting = await NextAsync(changes, session => session.SessionId == created.Id && session.Status == HostSessionStatuses.WaitingForUser);
        Assert.Equal(1, waiting.PendingCount);
        Assert.Contains(await creator.ListHostSessionsAsync(Ct), session => session == waiting);

        var request = await NextAsync(creator.ApprovalsAsync(Ct));
        await creator.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct);
        await approval.WaitAsync(Arrival, Ct);
        var answered = await NextAsync(changes, session => session.SessionId == created.Id && session.PendingCount == 0);
        Assert.Equal(HostSessionStatuses.Idle, answered.Status);
    }

    [Fact(Timeout = 60000)]
    public async Task SessionRegistry_RecoversAfterHostRestart_AndReconcilesTerminalsWithTheDaemon()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhr", TestContext.Current.CancellationToken);
        var before = await host.ConnectAsync("before", Ct);
        var harness = await before.CreateSessionAsync(host.Environment.App.Home.Root, Selection, Spec, Ct);
        var ptyId = await before.StartPtyAsync(new RemotePtyStartRequest(Shell.FileName, Shell.Arguments, Title: "build shell"), Ct);

        await host.RestartHostAsync(Ct);

        var after = await host.ConnectAsync("after", Ct);
        var sessions = await after.ListHostSessionsAsync(Ct);
        var restoredHarness = Assert.Single(sessions, session => session.SessionId == harness.Id);
        Assert.Equal(HostSessionStatuses.Exited, restoredHarness.Status);
        var restoredTerminal = Assert.Single(sessions, session => session.SessionId == ptyId);
        Assert.Equal(HostSessionKinds.Terminal, restoredTerminal.Kind);
        Assert.Equal("build shell", restoredTerminal.Title);
        Assert.Equal(HostSessionStatuses.Running, restoredTerminal.Status);

        var changes = after.HostSessionChangesAsync(Ct);
        await after.ResumeSessionAsync(harness.Id, Selection, Spec, Ct);
        Assert.Equal(HostSessionStatuses.Idle, (await NextAsync(changes, session => session.SessionId == harness.Id)).Status);
        await after.StopPtyAsync(ptyId, Ct);
        Assert.Equal(HostSessionStatuses.Exited, (await NextAsync(changes, session => session.SessionId == ptyId)).Status);
    }

    [Fact(Timeout = 30000)]
    public async Task Handshake_AdvertisesCapabilities_AndReportsTheReleaseVersion()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhv", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("probe", Ct);

        Assert.Equal(HostProtocol.Version, client.Hello.ProtocolVersion);
        Assert.Equal(HostCapabilities.All, client.Hello.Capabilities);
        Assert.False(string.IsNullOrEmpty(client.Hello.ConnectionId));
        var info = await client.InfoAsync(Ct);
        Assert.Equal(RemoteHostServer.ReleaseVersion, info.HostVersion);
        Assert.Equal(HostCapabilities.All, info.Capabilities);
    }

    internal static readonly RemoteProviderSpec Spec = new("client-provider", "openai-compatible", "https://example.invalid", "sk-test");

    internal static readonly RemoteModelSelection Selection = new("client-provider", "client-model");

    internal static (string FileName, string[] Arguments) Shell
        => OperatingSystem.IsWindows() ? ("cmd.exe", ["/v:on"]) : ("/bin/sh", []);

    private static IAgent Agent(HarnessHostFixture host, string sessionId)
        => host.Environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(SessionId.Create(sessionId))!;

    /** 与 ApprovalService 派发应答者的方式相同: 在该 agent 的作用域上跑审批瀑布, 没有应答者时得到 Unavailable。 */
    private static async Task<ApprovalOutcome> RaiseApprovalAsync(HarnessHostFixture host, IAgent agent, string tool)
    {
        var ctx = host.Environment.App.Ctx;
        var request = new ApprovalRequest(agent, tool, new ToolCallId($"call-{Guid.NewGuid():N}"), "needs approval", "{}");
        var result = await ctx.Events.Waterfall(
            DshScope.ScopeTarget(ctx, agent.ScopeKey),
            new ApprovalRequestNotification(request),
            () => new ValueTask<object?>(ApprovalOutcome.Unavailable));
        return Assert.IsType<ApprovalOutcome>(result);
    }

    private static Task<AskUserQuestionAnswer> AskQuestionAsync(HarnessHostFixture host, IAgent agent)
        => host.Environment.App.Ctx.Get<UserQuestionService>(UserQuestionService.ServiceName)!.Ask(
            new AskUserQuestionRequest([new AskUserQuestionItem("q1", "pick one", Options: [new AskUserQuestionOption("A"), new AskUserQuestionOption("B")])], agent),
            Ct);

    internal static async Task<T> NextAsync<T>(IAsyncEnumerable<T> stream, Func<T, bool>? match = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Arrival);
        await foreach (var item in stream.WithCancellation(timeout.Token))
        {
            if (match is null || match(item))
                return item;
        }
        throw new InvalidOperationException("stream ended before the expected item arrived");
    }
}
