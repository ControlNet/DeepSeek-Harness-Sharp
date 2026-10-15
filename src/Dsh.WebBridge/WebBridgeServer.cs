using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.WebBridge;

/** HostRoot is the harness home whose run/host.sock the bridge connects to; Port 0 picks a free port. */
public sealed record WebBridgeOptions(string? HostRoot, int Port);

/**
 * Loopback HTTP and WebSocket server that lets a browser speak the host's JSON-RPC protocol.
 * Routes: GET /ws (WebSocket relay to a fresh host.sock connection), POST /api/login (token for session cookie), GET /api/session (cookie check), GET anything else (embedded front end).
 * It is written directly on TcpListener plus WebSocket.CreateFromStream, so it needs neither the ASP.NET Core shared framework nor http.sys URL reservations.
 */
public sealed class WebBridgeServer : IAsyncDisposable
{
    public const string WebSocketPath = "/ws";
    public const string LoginPath = "/api/login";
    public const string SessionPath = "/api/session";

    /** RFC 6455 key suffix hashed into Sec-WebSocket-Accept. */
    private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const string SupportedWebSocketVersion = "13";

    /** A client must deliver its whole request within this time, so idle connections cannot pile up. */
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /** Unsolicited pongs keep NAT and SSH forwards from dropping an idle WebSocket. */
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener;
    private readonly LocalEndpointToken _token;
    private readonly WebAccessGate _gate;
    private readonly string _portFile;
    private readonly Func<CancellationToken, Task<Stream>> _connectHost;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private int _disposed;

    private WebBridgeServer(TcpListener listener, LocalEndpointToken token, string portFile, Func<CancellationToken, Task<Stream>> connectHost)
    {
        _listener = listener;
        _token = token;
        _gate = new WebAccessGate(token);
        _portFile = portFile;
        _connectHost = connectHost;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
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
            return new WebBridgeServer(listener, token, portFile, cancellationToken => RemoteHostListener.ConnectAsync(options.HostRoot, cancellationToken));
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

    private async Task UpgradeAsync(Stream stream, HttpRequest request, string host, CancellationToken cancellationToken)
    {
        if (UpgradeRefusal(request, host) is (var status, var reason))
        {
            await HttpResponse.WriteTextAsync(stream, status, reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes($"{request.Header("Sec-WebSocket-Key")}{WebSocketAcceptGuid}")));
        var head = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = KeepAliveInterval });
        await HostRelay.RunAsync(socket, _connectHost, cancellationToken).ConfigureAwait(false);
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
