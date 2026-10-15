using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Dsh.Transport;

namespace Dsh.WebBridge;

/**
 * Relays one browser WebSocket to one host.sock connection without looking inside the JSON-RPC messages.
 * Each WebSocket text message becomes one length-prefixed frame towards the host, and each host frame becomes one text message towards the browser.
 * Whichever side ends first ends the other, so a closed tab releases its host connection and a stopped host closes the tab's socket.
 */
internal static class HostRelay
{
    private const int ReceiveChunkBytes = 16 * 1024;

    /** RFC 6455 caps a close frame's reason at 123 UTF-8 bytes. */
    private const int MaxCloseReasonBytes = 123;

    /** How long the bridge waits to deliver its close frame before it drops the socket. */
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    /** Why the relay ended; a null status means the socket broke and no close frame can be sent. */
    private readonly record struct RelayEnd(WebSocketCloseStatus? Status, string Reason);

    /** Close reasons reach the browser, so each passes through redact first (connection failures may quote ssh or proxy command lines). */
    public static async Task RunAsync(WebSocket socket, Func<CancellationToken, Task<Stream>> connectHost, Func<string, string> redact, CancellationToken cancellationToken)
    {
        Stream host;
        try
        {
            host = await connectHost(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or SocketException or InvalidOperationException or TimeoutException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            await CloseAsync(socket, new RelayEnd(WebSocketCloseStatus.InternalServerError, redact(error.Message))).ConfigureAwait(false);
            return;
        }

        using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var toHost = PumpToHostAsync(socket, host, relay.Token);
        var toBrowser = PumpToBrowserAsync(host, socket, relay.Token);
        var end = await await Task.WhenAny(toHost, toBrowser).ConfigureAwait(false);
        // Releasing the host connection first also ends the host-side pump.
        await host.DisposeAsync().ConfigureAwait(false);
        await CloseAsync(socket, end with { Reason = redact(end.Reason) }).ConfigureAwait(false);
        await relay.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(toHost, toBrowser).ConfigureAwait(false);
    }

    private static async Task<RelayEnd> PumpToHostAsync(WebSocket socket, Stream host, CancellationToken cancellationToken)
    {
        var chunk = new byte[ReceiveChunkBytes];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (received.MessageType != WebSocketMessageType.Text)
                    return EndOfBrowserMessages(received.MessageType);
                if (message.Length + received.Count > FrameCodec.DefaultMaxFrameBytes)
                    return new RelayEnd(WebSocketCloseStatus.MessageTooBig, "message exceeds the host frame limit");
                message.Write(chunk, 0, received.Count);
                if (!received.EndOfMessage)
                    continue;
                await FrameCodec.WriteFrameAsync(host, message.GetBuffer().AsMemory(0, (int)message.Length), cancellationToken).ConfigureAwait(false);
                message.SetLength(0);
            }
        }
        catch (Exception error) when (IsConnectionEnd(error))
        {
            return new RelayEnd(null, "");
        }
    }

    private static RelayEnd EndOfBrowserMessages(WebSocketMessageType type)
        => type == WebSocketMessageType.Close
            ? new RelayEnd(WebSocketCloseStatus.NormalClosure, "")
            : new RelayEnd(WebSocketCloseStatus.InvalidMessageType, "only text messages carrying JSON-RPC are accepted");

    private static async Task<RelayEnd> PumpToBrowserAsync(Stream host, WebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (await FrameCodec.ReadFrameAsync(host, FrameCodec.DefaultMaxFrameBytes, cancellationToken).ConfigureAwait(false) is { } frame)
                await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            return new RelayEnd(WebSocketCloseStatus.EndpointUnavailable, "host closed the connection");
        }
        catch (Exception error) when (IsConnectionEnd(error))
        {
            return new RelayEnd(WebSocketCloseStatus.EndpointUnavailable, "host connection lost");
        }
    }

    /** Sends the close frame when the socket can still carry one; the browser's own close is answered the same way. */
    private static async Task CloseAsync(WebSocket socket, RelayEnd end)
    {
        if (end.Status is not { } status || socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await socket.CloseOutputAsync(status, FitCloseReason(end.Reason), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsConnectionEnd(error))
        {
        }
    }

    /** Long reasons (an ssh error, a deployment failure) are cut on a character boundary so the close frame stays valid. */
    private static string FitCloseReason(string reason)
    {
        var length = reason.Length;
        while (Encoding.UTF8.GetByteCount(reason.AsSpan(0, length)) > MaxCloseReasonBytes)
            length--;
        if (length < reason.Length && length > 0 && char.IsHighSurrogate(reason[length - 1]))
            length--;
        return reason[..length];
    }

    private static bool IsConnectionEnd(Exception error)
        => error is IOException or SocketException or WebSocketException or OperationCanceledException or ObjectDisposedException;
}
