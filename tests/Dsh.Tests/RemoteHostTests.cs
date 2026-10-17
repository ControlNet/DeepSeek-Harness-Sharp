using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

public sealed class RemoteHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HandshakeInfoAndPingRoundTrip()
    {
        await using var fixture = await HostFixture.CreateAsync(token: null, Ct);
        Assert.NotNull(fixture.Client);
        var info = await fixture.Client!.InfoAsync(Ct);
        Assert.Equal(HostProtocol.Version, info.ProtocolVersion);
        Assert.False(string.IsNullOrEmpty(info.HostVersion));
        Assert.True(await fixture.Client.PingAsync(Ct) > 0);
        Assert.True(fixture.Client.Hello.Ok);
    }

    [Fact]
    public async Task WrongTokenIsRejected()
    {
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var failed = await HostFixture.CreateAsync(token: "secret", Ct, connectToken: "wrong");
        });
        Assert.Contains("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MethodsBeforeHelloAreRejectedWhenTokenIsRequired()
    {
        await using var fixture = await HostFixture.CreateAsync(token: "secret", Ct, connect: false);
        await using var peer = new JsonRpcPeer(fixture.ClientStream);
        peer.Start();

        await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync(HostProtocol.MethodInfo, null, Ct));

        var hello = JsonSerializer.SerializeToElement(
            new HostHelloRequest("secret", HostProtocol.Version), HostProtocolJsonContext.Default.HostHelloRequest);
        await peer.RequestAsync(HostProtocol.MethodHello, hello, Ct);
        Assert.NotNull(await peer.RequestAsync(HostProtocol.MethodInfo, null, Ct));
    }

    [Fact]
    public async Task VersionMismatchIsRejected()
    {
        await using var fixture = await HostFixture.CreateAsync(token: null, Ct, connect: false);
        await using var peer = new JsonRpcPeer(fixture.ClientStream);
        peer.Start();
        var request = JsonSerializer.SerializeToElement(
            new HostHelloRequest(null, HostProtocol.Version + 999), HostProtocolJsonContext.Default.HostHelloRequest);
        var result = await peer.RequestAsync(HostProtocol.MethodHello, request, Ct);
        var hello = JsonSerializer.Deserialize(result!.Value, HostProtocolJsonContext.Default.HostHelloResponse)!;
        Assert.False(hello.Ok);
        Assert.Contains("version", hello.Mismatch!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 30000)]
    public async Task ClientRejectsAnAcceptedHelloWithTheWrongVersion()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var socket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        using var accepted = await accept;
        await using var peer = new JsonRpcPeer(accepted.GetStream());
        peer.RequestHandler = (_, _) => ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(
            new HostHelloResponse(true, HostProtocol.Version - 1, "old", null), HostProtocolJsonContext.Default.HostHelloResponse));
        peer.Start();

        var error = await Assert.ThrowsAsync<IOException>(() => RemoteHostClient.ConnectAsync(socket.GetStream(), cancellationToken: Ct));
        Assert.Contains("version", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 30000)]
    public async Task ResumeAndResizeErrorsAreNotCompatibilitySuccesses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var socket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        using var accepted = await accept;
        await using var peer = new JsonRpcPeer(accepted.GetStream());
        peer.RequestHandler = (message, _) =>
        {
            return message.Method switch
            {
                HostProtocol.MethodHello => ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(
                    new HostHelloResponse(true, HostProtocol.Version, "strict", null), HostProtocolJsonContext.Default.HostHelloResponse)),
                HostProtocol.MethodSessionResume => throw new JsonRpcException(-32602, "session already running"),
                _ => throw new JsonRpcException(JsonRpcPeer.MethodNotFound, "method not found")
            };
        };
        peer.Start();
        await using var client = await RemoteHostClient.ConnectAsync(socket.GetStream(), cancellationToken: Ct);

        var resume = await Assert.ThrowsAsync<JsonRpcException>(() => client.ResumeSessionAsync("session", cancellationToken: Ct));
        Assert.Contains("already", resume.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<JsonRpcException>(() => client.Pty.ResizePtyAsync("pty", 120, 40, Ct));
    }

    [Fact(Timeout = 30000)]
    public async Task DecisionsArrivingBeforeEnumerationAreRetained()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var socket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(cancellationToken);
        await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
        using var accepted = await accept;
        await using var peer = new JsonRpcPeer(accepted.GetStream());
        peer.RequestHandler = (message, _) => ValueTask.FromResult<JsonElement?>(
            message.Method == HostProtocol.MethodHello
                ? JsonSerializer.SerializeToElement(new HostHelloResponse(true, HostProtocol.Version, "test", null), HostProtocolJsonContext.Default.HostHelloResponse)
                : JsonSerializer.SerializeToElement(1L, HostProtocolJsonContext.Default.Int64));
        peer.Start();
        await using var client = await RemoteHostClient.ConnectAsync(socket.GetStream(), cancellationToken: cancellationToken);
        var approval = new RemoteApprovalRequest("approval", "session", "bash", null, "allow?");
        var question = new RemoteQuestionRequest("question", "session", []);
        var pending = new RemotePendingChange(new RemotePendingList([approval], [question]));
        await peer.NotifyAsync(HostProtocol.NotificationPendingChanged,
            JsonSerializer.SerializeToElement(pending, HostProtocolJsonContext.Default.RemotePendingChange), cancellationToken);
        // The ping reply follows the notification on the same TCP stream, so it arrived before enumeration begins.
        await client.PingAsync(cancellationToken);
        await using var changes = client.PendingChangesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(approval, Assert.Single(changes.Current.Pending.Approvals));
        Assert.Equal(question.RequestId, Assert.Single(changes.Current.Pending.Questions).RequestId);
        Assert.Null(changes.Current.Resolution);
    }

    /** 一对经 TCP loopback 相连的 host server / client。 */
    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly TcpClient _serverClient;
        private readonly TcpClient _client;
        private readonly Task _serve;

        private HostFixture(TcpClient serverClient, TcpClient client, Task serve, RemoteHostClient? hostClient, Stream clientStream)
        {
            _serverClient = serverClient;
            _client = client;
            _serve = serve;
            Client = hostClient;
            ClientStream = clientStream;
        }

        public RemoteHostClient? Client { get; }

        public Stream ClientStream { get; }

        public static async Task<HostFixture> CreateAsync(
            string? token,
            CancellationToken cancellationToken,
            string? connectToken = null,
            bool connect = true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            var serverClient = await acceptTask;
            listener.Stop();

            var server = new RemoteHostServer(new RemoteHostServerOptions(token));
            var clientStream = client.GetStream();
            var serve = Task.Run(() => server.ServeAsync(serverClient.GetStream(), cancellationToken), cancellationToken);
            var hostClient = connect
                ? await RemoteHostClient.ConnectAsync(clientStream, connectToken ?? token, cancellationToken)
                : null;
            return new HostFixture(serverClient, client, serve, hostClient, clientStream);
        }

        public async ValueTask DisposeAsync()
        {
            if (Client is not null)
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
