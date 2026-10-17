using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

public sealed class RemoteHostCapabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30000)]
    public async Task CreateSessionForwardsModelSelectionOverTheWire()
    {
        await using var backend = await FakeBackend.CreateAsync(Ct);
        await using var fixture = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        var selection = new RemoteModelSelection("prov", "model", "high");
        var spec = new RemoteProviderSpec("prov", "openai-compatible", "https://example.invalid", "sk", "PROV_KEY");

        var created = await fixture.Client.CreateSessionAsync("/work", selection, spec, Ct);
        Assert.Equal("prov", backend.LastSelection?.Provider);
        Assert.Equal("model", backend.LastSelection?.Model);
        Assert.Equal("high", backend.LastSelection?.ReasoningEffort);
        Assert.Equal(spec, backend.LastProvider);

        await fixture.Client.ResumeSessionAsync(created.Id, selection, spec, Ct);
        Assert.Equal(spec, backend.LastProvider);
    }

    [Fact(Timeout = 30000)]
    public async Task SessionsToolsFilesAndMessageFlowOverTheWire()
    {
        await using var backend = await FakeBackend.CreateAsync(Ct);
        await using var fixture = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        var client = fixture.Client;

        Assert.Empty(await client.ListSessionsAsync(Ct));
        var created = await client.CreateSessionAsync("/work", cancellationToken: Ct);
        Assert.Equal("/work", created.Cwd);
        Assert.Single(await client.ListSessionsAsync(Ct));
        Assert.Equal(["read", "write"], await client.ListToolsAsync(created.Id, Ct));

        var work = Path.Combine(backend.Root, "work");
        Directory.CreateDirectory(Path.Combine(work, "sub"));
        await client.WriteFileAsync(Path.Combine(work, "a.txt"), "hello"u8.ToArray(), Ct);
        Assert.Equal("hello"u8.ToArray(), await client.ReadFileAsync(Path.Combine(work, "a.txt"), Ct));

        var listing = await client.ListDirectoryAsync(work, Ct);
        Assert.Equal(work, listing.Path);
        Assert.Equal(backend.Root, listing.Parent);
        Assert.Contains(listing.Entries, entry => entry is { Name: "sub", IsDirectory: true });

        var received = new TaskCompletionSource<RemoteEventInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = Task.Run(async () =>
        {
            await foreach (var item in client.SubscribeAsync(created.Id, 0, subscription.Token))
                received.TrySetResult(item);
        }, Ct);
        // host 应答 subscribe 之前后端已挂好订阅: 等后端看到订阅即可发消息, 不靠固定延时。
        await backend.Subscribed.Task.WaitAsync(Ct);
        await client.SendMessageAsync(created.Id, "hi", [], Ct);
        var item = await received.Task.WaitAsync(Ct);
        var message = DshJson.Deserialize<SessionEvent>(item.EventJson)!;
        Assert.Equal(0L, item.Sequence);
        Assert.Equal(item.Sequence, message.Seq);
        Assert.Equal("hi", Assert.IsType<TextBlock>(Assert.Single(Assert.IsType<UserMessagePayload>(message.Data).Message.Content)).Text);
        await subscription.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ApprovalRequestRoundTripsBack()
    {
        await using var backend = await FakeBackend.CreateAsync(Ct);
        await using var fixture = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        var received = new TaskCompletionSource<RemoteApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = Task.Run(async () =>
        {
            await foreach (var change in fixture.Client.PendingChangesAsync(subscription.Token))
            {
                foreach (var request in change.Pending.Approvals)
                    received.TrySetResult(request);
            }
        }, Ct);

        var decision = backend.Pending.AskApprovalAsync(new RemoteApprovalRequest("req-1", "session-1", "bash", null, "allow?"));
        var approval = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(await fixture.Client.RespondApprovalAsync(approval.RequestId, true, "ok", Ct));
        Assert.True((await decision.WaitAsync(TimeSpan.FromSeconds(5), Ct))?.Allow);
        await subscription.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact(Timeout = 30000)]
    public async Task PtyEchoesInputOverTheWire()
    {
        await using var backend = await FakeBackend.CreateAsync(Ct);
        await using var fixture = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        var ptyId = await fixture.Client.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", []), Ct);

        var pipe = new System.IO.Pipelines.Pipe();
        var input = pipe.Reader.AsStream();
        var output = new MemoryStream();
        using var attachCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var attach = fixture.Client.AttachPtyAsync(ptyId, input, output, attachCts.Token);

        await pipe.Writer.WriteAsync("ping"u8.ToArray(), Ct);
        await pipe.Writer.FlushAsync(Ct);
        await WaitForAsync(() => output.Length >= 4, Ct);
        Assert.Equal("ping", Encoding.UTF8.GetString(output.ToArray()));

        await attachCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attach);
    }

    [Fact(Timeout = 30000)]
    public async Task QuestionRequestRoundTripsBack()
    {
        await using var backend = await FakeBackend.CreateAsync(Ct);
        await using var fixture = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        var received = new TaskCompletionSource<RemoteQuestionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = Task.Run(async () =>
        {
            await foreach (var change in fixture.Client.PendingChangesAsync(subscription.Token))
            {
                foreach (var request in change.Pending.Questions)
                    received.TrySetResult(request);
            }
        }, Ct);

        var answer = backend.Pending.AskQuestionAsync(new RemoteQuestionRequest("q-1", "session-1",
            [new RemoteQuestionItem("q1", "pick?", null, null, [new RemoteQuestionOption("A", null)], false, null)]));
        var question = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(await fixture.Client.RespondQuestionAsync(question.RequestId, [new RemoteQuestionAnswerItem("q1", ["A"], null)], Ct));
        Assert.Equal(["A"], (await answer.WaitAsync(TimeSpan.FromSeconds(5), Ct))!.Answers[0].Selected);
        await subscription.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(10, cancellationToken);
        Assert.True(condition(), "condition not met in time");
    }

    /** 记录/回放式的远端后端替身; 不依赖 harness。待决策队列、会话登记表、文件系统(限于 Root)与通知历史用真实实现。 */
    internal sealed class FakeBackend : IRemoteHostBackend, IAsyncDisposable
    {
        private readonly string _directory;
        private readonly Dictionary<string, RemoteSessionInfo> _sessions = [];
        private readonly Dictionary<string, Channel<RemoteEventInfo>> _events = [];
        private readonly Dictionary<string, long> _sequences = [];
        private readonly Dictionary<string, Channel<RemotePtyOutput>> _pty = [];

        private FakeBackend(string directory, HostPendingDecisions pending, HostSessionRegistry sessions, HostNotificationCenter notifications)
        {
            _directory = directory;
            Pending = pending;
            Sessions = sessions;
            Notifications = notifications;
            Files = new HostFileSystem(new HostPathPolicy([directory], () => []));
        }

        public static async Task<FakeBackend> CreateAsync(CancellationToken cancellationToken)
        {
            var directory = TempTree.CreateDirectory("rh-cap");
            var pending = new HostPendingDecisions();
            var sessions = await HostSessionRegistry.OpenAsync(Path.Combine(directory, "sessions.json"), pending, cancellationToken);
            var notifications = await HostNotificationCenter.OpenAsync(Path.Combine(directory, HostNotificationCenter.FileName), sessions, pending, cancellationToken);
            return new FakeBackend(directory, pending, sessions, notifications);
        }

        /** The only directory the fake host's file system may touch. */
        public string Root => _directory;

        public HostPendingDecisions Pending { get; }

        public HostSessionRegistry Sessions { get; }

        public HostFileSystem Files { get; }

        public HostNotificationCenter Notifications { get; }

        /** 后端完成订阅(挂好事件通道)时置位。 */
        public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
            => Task.FromResult(new HostInfo(HostProtocol.Version, "fake", "test", "/home"));

        public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteSessionInfo>>([.. _sessions.Values]);

        public Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken)
        {
            LastSelection = selection;
            LastProvider = provider;
            var session = new RemoteSessionInfo($"session-{_sessions.Count + 1}", cwd, "Idle", 0, null, null, 0);
            _sessions[session.Id] = session;
            // Events are kept from creation on, like a real session log that a subscription from seq 0 replays.
            _events[session.Id] = Channel.CreateUnbounded<RemoteEventInfo>();
            return Task.FromResult(session);
        }

        public Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken)
        {
            LastSelection = selection;
            LastProvider = provider;
            return Task.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : throw new KeyNotFoundException(sessionId));
        }

        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListReasoningEffortsAsync(string provider, string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListProviderModelsAsync(string provider, CancellationToken cancellationToken) => throw new NotSupportedException();

        public RemoteModelSelection? LastSelection { get; private set; }

        public RemoteProviderSpec? LastProvider { get; private set; }

        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
        {
            var seq = _sequences.GetValueOrDefault(sessionId);
            _sequences[sessionId] = seq + 1;
            var item = new SessionEvent
            {
                Type = SessionEventTypes.UserMessage,
                Seq = seq,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = new UserMessagePayload(MessageFactory.CreateUserText(text)),
                SurfaceOp = new SurfaceOp.Append(),
            };
            _events[sessionId].Writer.TryWrite(new RemoteEventInfo(sessionId, DshJson.Serialize(item), seq));
            return Task.CompletedTask;
        }

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(["read", "write"]);

        public Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteHostSession> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task StopAgentAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteHostSession> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken)
        {
            var id = $"pty-{Guid.NewGuid():N}";
            _pty[id] = Channel.CreateUnbounded<RemotePtyOutput>();
            return Task.FromResult(id);
        }

        public Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken)
        {
            if (_pty.TryGetValue(ptyId, out var channel))
                channel.Writer.TryWrite(new RemotePtyOutput(ptyId, Convert.ToBase64String(data), false));
            return Task.CompletedTask;
        }

        public Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken)
        {
            if (_pty.Remove(ptyId, out var channel))
                channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public Task<IAsyncEnumerable<RemoteEventInfo>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
        {
            var channel = _events.TryGetValue(sessionId, out var existing)
                ? existing
                : _events[sessionId] = Channel.CreateUnbounded<RemoteEventInfo>();
            Subscribed.TrySetResult();
            return Task.FromResult(channel.Reader.ReadAllAsync(cancellationToken));
        }

        public Task<IAsyncEnumerable<RemotePtyOutput>> AttachPtyAsync(string ptyId, CancellationToken cancellationToken)
            => _pty.TryGetValue(ptyId, out var channel)
                ? Task.FromResult(channel.Reader.ReadAllAsync(cancellationToken))
                : throw new InvalidOperationException($"PTY session not found: {ptyId}");

        /** Every live host connection holds one pending-decision subscription, so this counts open host connections. */
        public int OpenConnections => Pending.SubscriberCount;

        public async ValueTask DisposeAsync()
        {
            await Notifications.DisposeAsync();
            Pending.Dispose();
            await Sessions.DisposeAsync();
            TempTree.Delete(_directory);
        }
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly TcpClient _serverClient;
        private readonly TcpClient _client;
        private readonly Task _serve;

        private HostFixture(TcpClient serverClient, TcpClient client, Task serve, RemoteHostClient hostClient)
        {
            _serverClient = serverClient;
            _client = client;
            _serve = serve;
            Client = hostClient;
        }

        public RemoteHostClient Client { get; }

        public static async Task<HostFixture> CreateAsync(IRemoteHostBackend backend, CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            var serverClient = await acceptTask;
            listener.Stop();

            var server = new RemoteHostServer(new RemoteHostServerOptions(null), backend);
            var serve = Task.Run(() => server.ServeAsync(serverClient.GetStream(), cancellationToken), cancellationToken);
            var hostClient = await RemoteHostClient.ConnectAsync(client.GetStream(), null, cancellationToken);
            return new HostFixture(serverClient, client, serve, hostClient);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _serverClient.Dispose();
            _client.Dispose();
            try
            {
                await _serve.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
            }
        }
    }
}
