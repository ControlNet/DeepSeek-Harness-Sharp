using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.WebBridge;

/**
 * HostRoot is the harness home of the bridge itself (its port and token files); Port 0 picks a free port.
 * Machines are the hosts the bridge reaches, the first one being the default of a WebSocket that names none; without them it is only the local host of HostRoot.
 * Fleet configures how remote machines are connected and deployed.
 */
public sealed record WebBridgeOptions(string? HostRoot, int Port, IReadOnlyList<MachineSpec>? Machines = null, FleetOptions? Fleet = null);

/**
 * Loopback HTTP and WebSocket server that lets a browser speak the host's JSON-RPC protocol to every machine of the fleet (hub mode).
 * Routes: GET /ws[?machine=name] (WebSocket relay to a fresh channel to that machine's host), GET /api/machines (machine list and connection states), POST /api/login (token for session cookie), GET /api/session (cookie check), GET anything else (embedded front end).
 * A FleetClient keeps one status connection per machine and deploys remote dsharp when needed; browser traffic never goes through it.
 * It is written directly on TcpListener plus WebSocket.CreateFromStream, so it needs neither the ASP.NET Core shared framework nor http.sys URL reservations.
 */
public sealed class WebBridgeServer : IAsyncDisposable
{
    public const string WebSocketPath = "/ws";
    public const string LoginPath = "/api/login";
    public const string SessionPath = "/api/session";
    public const string MachinesPath = "/api/machines";

    /** Query parameter of /ws that names the target machine. */
    public const string MachineParameter = "machine";

    /** Shown by hosts as the one who answered when the bridge itself would answer; browsers name themselves in host.hello. */
    public static readonly string DefaultClientName = $"web@{Environment.MachineName}";

    /** RFC 6455 key suffix hashed into Sec-WebSocket-Accept. */
    private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const string SupportedWebSocketVersion = "13";

    /** A client must deliver its whole request within this time, so idle connections cannot pile up. */
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /** Unsolicited pongs keep NAT and SSH forwards from dropping an idle WebSocket. */
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    /** How long a WebSocket waits for a remote machine that is still connecting or deploying before it is closed. */
    private static readonly TimeSpan ChannelOpenTimeout = TimeSpan.FromSeconds(60);

    private readonly TcpListener _listener;
    private readonly LocalEndpointToken _token;
    private readonly WebAccessGate _gate;
    private readonly string _portFile;
    private readonly FleetClient _fleet;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private int _disposed;

    private WebBridgeServer(TcpListener listener, LocalEndpointToken token, string portFile, FleetClient fleet)
    {
        _listener = listener;
        _token = token;
        _gate = new WebAccessGate(token);
        _portFile = portFile;
        _fleet = fleet;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        fleet.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /**
     * Binds the loopback port first, so a busy port fails before anything is written.
     * The token is issued before the port file announces the bridge and before the first connection is accepted.
     */
    public static WebBridgeServer Start(WebBridgeOptions options)
    {
        var listener = new TcpListener(IPAddress.Loopback, options.Port);
        listener.Start();
        try
        {
            var portFile = WebBridgeEndpoint.PortFile(options.HostRoot);
            var token = LocalEndpointToken.Issue(portFile);
            OwnerOnlyFiles.Write(portFile, ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture));
            var fleet = new FleetClient(options.Machines ?? [MachineSpec.Local(options.HostRoot)], options.Fleet ?? new FleetOptions { ClientName = DefaultClientName });
            return new WebBridgeServer(listener, token, portFile, fleet);
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }

            // Every connection is served on its own, so a slow client cannot block the others.
            _ = ServeClientAsync(client);
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                await HandleAsync(stream, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException)
            {
            }
        }
    }

    private async Task HandleAsync(Stream stream, CancellationToken cancellationToken)
    {
        HttpRequest? request;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(RequestTimeout);
            try
            {
                request = await HttpRequestReader.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                await HttpResponse.WriteTextAsync(stream, HttpStatus.BadRequest, error.Message, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (request is not null)
            await RouteAsync(stream, request, cancellationToken).ConfigureAwait(false);
    }

    private Task RouteAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Header("Host") is not { } host || !WebAccessGate.IsAllowedHost(host))
            return HttpResponse.WriteTextAsync(stream, HttpStatus.Forbidden, "the Host header must name a loopback address", cancellationToken);
        return (request.Method, request.Path) switch
        {
            ("GET", WebSocketPath) => UpgradeAsync(stream, request, host, cancellationToken),
            ("POST", LoginPath) => LoginAsync(stream, request, host, cancellationToken),
            ("GET", MachinesPath) => MachinesAsync(stream, request, cancellationToken),
            ("GET", SessionPath) => HttpResponse.WriteTextAsync(
                stream, _gate.HasSession(request.Header("Cookie")) ? HttpStatus.NoContent : HttpStatus.Unauthorized, "", cancellationToken),
            ("GET", _) => ServeAssetAsync(stream, request.Path, cancellationToken),
            _ => HttpResponse.WriteTextAsync(stream, HttpStatus.MethodNotAllowed, "method not allowed", cancellationToken),
        };
    }

    private Task LoginAsync(Stream stream, HttpRequest request, string host, CancellationToken cancellationToken)
    {
        if (!WebAccessGate.IsSameOrigin(request.Header("Origin"), host))
            return HttpResponse.WriteTextAsync(stream, HttpStatus.Forbidden, "cross-origin login refused", cancellationToken);
        return _gate.Login(request.Body) is { } cookie
            ? HttpResponse.WriteAsync(stream, HttpStatus.NoContent, ReadOnlyMemory<byte>.Empty, HttpResponse.PlainText, $"Set-Cookie: {cookie}\r\n", cancellationToken)
            : HttpResponse.WriteTextAsync(stream, HttpStatus.Unauthorized, "invalid web access token", cancellationToken);
    }

    /** Machine names, kinds, targets and connection states only: the specs' passwords, key paths, proxies and ssh options never leave the bridge, and credentials in failure details are masked. */
    private Task MachinesAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!_gate.HasSession(request.Header("Cookie")))
            return HttpResponse.WriteTextAsync(stream, HttpStatus.Unauthorized, "log in first", cancellationToken);
        IReadOnlyList<FleetMachineStatus> statuses = [.. _fleet.Statuses().Zip(_fleet.Machines, (status, connection) => CredentialRedaction.Redact(status, connection.Machine))];
        var body = JsonSerializer.SerializeToUtf8Bytes(statuses, WebBridgeJsonContext.Default.IReadOnlyListFleetMachineStatus);
        return HttpResponse.WriteAsync(stream, HttpStatus.Ok, body, HttpResponse.Json, "", cancellationToken);
    }

    private async Task UpgradeAsync(Stream stream, HttpRequest request, string host, CancellationToken cancellationToken)
    {
        if (UpgradeRefusal(request, host) is (var status, var reason))
        {
            await HttpResponse.WriteTextAsync(stream, status, reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Target(request) is not { } machine)
        {
            await HttpResponse.WriteTextAsync(stream, HttpStatus.NotFound, "no such machine", cancellationToken).ConfigureAwait(false);
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes($"{request.Header("Sec-WebSocket-Key")}{WebSocketAcceptGuid}")));
        var head = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = KeepAliveInterval });
        await HostRelay.RunAsync(socket, token => OpenChannelAsync(machine, token), reason => CredentialRedaction.Redact(reason, machine.Machine), cancellationToken).ConfigureAwait(false);
    }

    /** The machine named by ?machine=, or the first (local) machine when the query names none; null for an unknown name. */
    private MachineConnection? Target(HttpRequest request)
        => request.QueryValue(MachineParameter) is { Length: > 0 } name ? _fleet.Find(name) : _fleet.Machines[0];

    /** A remote machine that is still connecting or deploying gets a bounded wait; running out of it reads as a timeout, not as the bridge stopping. */
    private static async Task<Stream> OpenChannelAsync(MachineConnection machine, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ChannelOpenTimeout);
        try
        {
            return await machine.OpenChannelAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{machine.Machine.Name} did not connect within {ChannelOpenTimeout.TotalSeconds:0} s ({machine.State}: {machine.Detail})");
        }
    }

    /** Origin first, so a cross-site page learns nothing about the session; then the session; then the handshake itself. */
    private (HttpStatus Status, string Reason)? UpgradeRefusal(HttpRequest request, string host)
    {
        if (!WebAccessGate.IsSameOrigin(request.Header("Origin"), host))
            return (HttpStatus.Forbidden, "cross-origin WebSocket refused");
        if (!_gate.HasSession(request.Header("Cookie")))
            return (HttpStatus.Unauthorized, "log in first");
        return IsWebSocketHandshake(request) ? null : (HttpStatus.BadRequest, "not a WebSocket version 13 handshake");
    }

    private static bool IsWebSocketHandshake(HttpRequest request)
        => request.Header("Sec-WebSocket-Key") is { Length: > 0 }
            && HasToken(request.Header("Upgrade"), "websocket")
            && HasToken(request.Header("Connection"), "upgrade")
            && request.Header("Sec-WebSocket-Version") == SupportedWebSocketVersion;

    /** Connection may list several tokens, e.g. "keep-alive, Upgrade". */
    private static bool HasToken(string? header, string token)
        => header is not null && header.Split(',', StringSplitOptions.TrimEntries).Contains(token, StringComparer.OrdinalIgnoreCase);

    private static async Task ServeAssetAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        if (await StaticAssets.LoadAsync(path, cancellationToken).ConfigureAwait(false) is { } asset)
            await HttpResponse.WriteAsync(stream, HttpStatus.Ok, asset.Content, asset.ContentType, "", cancellationToken).ConfigureAwait(false);
        else
            await HttpResponse.WriteTextAsync(stream, HttpStatus.NotFound, "not found", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // Cancelling also ends every open relay, which releases its host connection.
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _acceptLoop.ConfigureAwait(false);
        await _fleet.DisposeAsync().ConfigureAwait(false);
        try
        {
            File.Delete(_portFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        _token.Dispose();
    }
}
