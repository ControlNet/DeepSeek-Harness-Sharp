using System.Net;
using System.Net.Sockets;
using Dsh.RemoteHost;

namespace Dsh.Tests;

public sealed class RemoteHostPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /** 常驻 daemon + stdio 桥: 客户端断开后再接回来, 会话仍在(脱钩/重连)。 */
    [Fact(Timeout = 30000)]
    public async Task SessionsSurviveClientReconnectThroughBridge()
    {
        // 套接字路径有 108 字符上限: 用仓库内浅目录, 否则 <root>/run/host.sock 超长, daemon 绑不上。
        var root = TempTree.CreateSocketDirectory("rh");
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(root));
        await using var backend = await SessionBackend.CreateAsync(root, Ct);
        var server = new RemoteHostServer(new RemoteHostServerOptions(null), backend);
        using var daemonCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var daemon = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(server, root, daemonCts.Token), Ct);
        try
        {
            await using (var first = await ConnectViaBridgeAsync(root, Ct))
            {
                var created = await first.Client.CreateSessionAsync("/work", cancellationToken: Ct);
                Assert.Equal("/work", created.Cwd);
            }

            // 第一次的客户端/桥都已断开, daemon 仍在; 重新连入应看到同一会话。
            await using var second = await ConnectViaBridgeAsync(root, Ct);
            var sessions = await second.Client.ListSessionsAsync(Ct);
            Assert.Contains(sessions, session => session.Cwd == "/work");
        }
        finally
        {
            await daemonCts.CancelAsync();
            try
            {
                await daemon;
            }
            catch (OperationCanceledException)
            {
            }

            TempTree.Delete(root);
        }
    }

    /** 半开/卡住的连接不能阻塞后续连接: 每条连接必须独立服务。 */
    [Fact(Timeout = 30000)]
    public async Task StuckConnectionDoesNotBlockNewConnections()
    {
        // 套接字路径有 108 字符上限: 用仓库内浅目录, 否则 <root>/run/host.sock 超长, daemon 绑不上。
        var root = TempTree.CreateSocketDirectory("rh");
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(root));
        await using var backend = await SessionBackend.CreateAsync(root, Ct);
        var server = new RemoteHostServer(new RemoteHostServerOptions(null), backend);
        using var daemonCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var daemon = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(server, root, daemonCts.Token), Ct);
        try
        {
            // 第一条连接建立后不发任何数据(半开): 旧实现串行服务, 会让 daemon 卡在这条连接上。
            await using var stuck = await RemoteHostListener.ConnectAsync(root, Ct);

            // 第二条连接应能正常完成握手与请求。
            await using var stream = await RemoteHostListener.ConnectAsync(root, Ct);
            await using var client = await RemoteHostClient.ConnectAsync(stream, null, Ct);
            var info = await client.InfoAsync(Ct);
            Assert.Equal(HostProtocol.Version, info.ProtocolVersion);
        }
        finally
        {
            await daemonCts.CancelAsync();
            try
            {
                await daemon;
            }
            catch (OperationCanceledException)
            {
            }

            TempTree.Delete(root);
        }
    }

    /** 模拟 stdio-over-SSH: 一端是“ssh 客户端”, 另一端交给桥转发到 daemon。 */
    private static async Task<BridgeConnection> ConnectViaBridgeAsync(string root, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync(cancellationToken);
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        var bridge = await acceptTask;
        listener.Stop();

        var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bridgeTask = Task.Run(() => RemoteHostListener.BridgeAsync(bridge.GetStream(), root, bridgeCts.Token), cancellationToken);
        var hostClient = await RemoteHostClient.ConnectAsync(client.GetStream(), null, cancellationToken);
        return new BridgeConnection(hostClient, client, bridge, bridgeTask, bridgeCts);
    }

    private sealed class BridgeConnection(
        RemoteHostClient client,
        TcpClient clientTcp,
        TcpClient bridgeTcp,
        Task bridgeTask,
        CancellationTokenSource bridgeCts) : IAsyncDisposable
    {
        public RemoteHostClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            clientTcp.Dispose();
            bridgeTcp.Dispose();
            await bridgeCts.CancelAsync();
            try
            {
                await bridgeTask;
            }
            catch (OperationCanceledException)
            {
            }

            bridgeCts.Dispose();
        }
    }

    /** 只实现本用例用到的会话能力; 其余为只读/空实现。 */
    private sealed class SessionBackend : IRemoteHostBackend, IAsyncDisposable
    {
        private readonly Dictionary<string, RemoteSessionInfo> _sessions = [];

        private SessionBackend(string root, HostPendingDecisions pending, HostSessionRegistry sessions, HostNotificationCenter notifications)
        {
            Pending = pending;
            Sessions = sessions;
            Notifications = notifications;
            Files = new HostFileSystem(new HostPathPolicy([root], () => []));
        }

        public static async Task<SessionBackend> CreateAsync(string root, CancellationToken cancellationToken)
        {
            var pending = new HostPendingDecisions();
            var sessions = await HostSessionRegistry.OpenAsync(Path.Combine(root, "sessions.json"), pending, cancellationToken);
            var notifications = await HostNotificationCenter.OpenAsync(Path.Combine(root, HostNotificationCenter.FileName), sessions, pending, cancellationToken);
            return new SessionBackend(root, pending, sessions, notifications);
        }

        public HostPendingDecisions Pending { get; }

        public HostSessionRegistry Sessions { get; }

        public HostFileSystem Files { get; }

        public HostNotificationCenter Notifications { get; }

        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
            => Task.FromResult(new HostInfo(HostProtocol.Version, "test", "test", "/home"));

        public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteSessionInfo>>([.. _sessions.Values]);

        public Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken)
        {
            var session = new RemoteSessionInfo($"session-{_sessions.Count + 1}", cwd, "Idle", 0, null, null, 0);
            _sessions[session.Id] = session;
            return Task.FromResult(session);
        }

        public Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken)
            => Task.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : throw new KeyNotFoundException(sessionId));

        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteHostSession> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task StopAgentAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteHostSession> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken) => Task.FromResult("");

        public Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IAsyncEnumerable<RemoteEventInfo>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
            => Task.FromResult(AsyncEnumerable.Empty<RemoteEventInfo>());

        public Task<IAsyncEnumerable<RemotePtyOutput>> AttachPtyAsync(string ptyId, CancellationToken cancellationToken)
            => Task.FromResult(AsyncEnumerable.Empty<RemotePtyOutput>());

        public async ValueTask DisposeAsync()
        {
            await Notifications.DisposeAsync();
            Pending.Dispose();
            await Sessions.DisposeAsync();
        }
    }
}
