using System.Text;

namespace Dsh.Transport;

/** An HTTP status line: code plus reason phrase. */
public readonly record struct HttpStatus(int Code, string Reason)
{
    public static readonly HttpStatus Ok = new(200, "OK");
    public static readonly HttpStatus NoContent = new(204, "No Content");
    public static readonly HttpStatus BadRequest = new(400, "Bad Request");
    public static readonly HttpStatus Unauthorized = new(401, "Unauthorized");
    public static readonly HttpStatus Forbidden = new(403, "Forbidden");
    public static readonly HttpStatus NotFound = new(404, "Not Found");
    public static readonly HttpStatus MethodNotAllowed = new(405, "Method Not Allowed");
    public static readonly HttpStatus InternalServerError = new(500, "Internal Server Error");
}

/**
 * Writes one complete HTTP/1.1 response and asks the client to close the connection, so each connection carries exactly one request.
 * Every response carries the same hardening headers: no sniffing, no referrer, no framing, and a same-origin-only content security policy.
 */
public static class HttpResponse
{
    public const string PlainText = "text/plain; charset=utf-8";
    public const string Json = "application/json; charset=utf-8";

    /**
     * connect-src 'self' also admits the same-origin ws:// and wss:// endpoint.
     * Scripts stay same-origin only, with neither inline scripts nor eval.
     * style-src admits inline styles because xterm.js injects generated <style> elements for its cell metrics, theme and scrollbar; without them the terminal renders unsized and uncoloured.
     * Inline styles cannot run script, and the page never inserts host text as markup.
     */
    private const string SecurityHeaders =
        "X-Content-Type-Options: nosniff\r\n"
        + "Referrer-Policy: no-referrer\r\n"
        + "X-Frame-Options: DENY\r\n"
        + "Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'\r\n";

    public static Task WriteTextAsync(Stream stream, HttpStatus status, string text, CancellationToken cancellationToken)
        => WriteAsync(stream, status, Encoding.UTF8.GetBytes(text), PlainText, "", cancellationToken);

    public static async Task WriteAsync(
        Stream stream,
        HttpStatus status,
        ReadOnlyMemory<byte> body,
        string contentType,
        string extraHeaders,
        CancellationToken cancellationToken)
    {
        await WriteHeadAsync(stream, status, contentType, body.Length, extraHeaders, cancellationToken).ConfigureAwait(false);
        if (!body.IsEmpty)
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /** A null length delimits a streaming response by closing the connection after its last event. */
    public static Task WriteHeadAsync(Stream stream, HttpStatus status, string contentType, int? contentLength, string extraHeaders, CancellationToken cancellationToken)
    {
        var lengthHeader = contentLength is { } length ? $"Content-Length: {length}\r\n" : "";
        var head = $"HTTP/1.1 {status.Code} {status.Reason}\r\nContent-Type: {contentType}\r\n{lengthHeader}Cache-Control: no-cache\r\n{SecurityHeaders}{extraHeaders}Connection: close\r\n\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).AsTask();
    }
}
