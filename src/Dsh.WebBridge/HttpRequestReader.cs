using System.Globalization;
using System.Text;

namespace Dsh.WebBridge;

/** One parsed HTTP/1.1 request: method, path without query, the raw query (without '?'), headers (case-insensitive, repeated ones joined by ", ") and the body. */
internal sealed record HttpRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);

    /** The decoded value of the first `name=value` query parameter, or null when the query has none. */
    public string? QueryValue(string name)
    {
        foreach (var pair in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0 && pair.AsSpan(0, equals).SequenceEqual(name))
                return Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }

        return null;
    }
}

/**
 * Minimal HTTP/1.1 request reader for the bridge's handful of routes.
 * It reads the head up to a fixed size, accepts only a Content-Length body of bounded size, and throws InvalidDataException for anything malformed.
 */
internal static class HttpRequestReader
{
    /** Upper bound of request line plus headers; browsers stay far below it. */
    public const int MaxHeadBytes = 16 * 1024;

    /** Upper bound of a request body; the only body the bridge accepts is the login form. */
    public const int MaxBodyBytes = 4 * 1024;

    private const int MaxHeaderLines = 100;

    private static readonly byte[] HeadTerminator = "\r\n\r\n"u8.ToArray();

    /** Returns null when the client closed the connection before sending anything. */
    public static async Task<HttpRequest?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeadBytes];
        var (filled, headLength) = await ReadHeadAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (filled == 0)
            return null;
        var lines = Encoding.Latin1.GetString(buffer, 0, headLength).Split("\r\n");
        var (method, path, query) = ParseRequestLine(lines[0]);
        var headers = ParseHeaders(lines.AsSpan(1));
        var leftover = buffer.AsMemory(headLength + HeadTerminator.Length, filled - headLength - HeadTerminator.Length);
        var body = await ReadBodyAsync(stream, headers, leftover, cancellationToken).ConfigureAwait(false);
        return new HttpRequest(method, path, query, headers, body);
    }

    /** Fills the buffer until the blank line that ends the head; returns the bytes read and the head's length without its terminator. */
    private static async Task<(int Filled, int HeadLength)> ReadHeadAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return filled == 0 ? (0, 0) : throw new InvalidDataException("connection closed inside the request head");
            filled += read;
            var end = buffer.AsSpan(0, filled).IndexOf(HeadTerminator);
            if (end >= 0)
                return (filled, end);
        }

        throw new InvalidDataException("request head too large");
    }

    private static (string Method, string Path, string Query) ParseRequestLine(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !parts[1].StartsWith('/'))
            throw new InvalidDataException("malformed request line");
        var query = parts[1].IndexOf('?');
        return query >= 0 ? (parts[0], parts[1][..query], parts[1][(query + 1)..]) : (parts[0], parts[1], "");
    }

    private static Dictionary<string, string> ParseHeaders(ReadOnlySpan<string> lines)
    {
        if (lines.Length > MaxHeaderLines)
            throw new InvalidDataException("too many request headers");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw new InvalidDataException("malformed request header");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var previous) ? $"{previous}, {value}" : value;
        }

        return headers;
    }

    private static async Task<byte[]> ReadBodyAsync(
        Stream stream,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> leftover,
        CancellationToken cancellationToken)
    {
        if (headers.ContainsKey("Transfer-Encoding"))
            throw new InvalidDataException("chunked request bodies are not supported");
        if (!headers.TryGetValue("Content-Length", out var declared))
            return [];
        if (!int.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > MaxBodyBytes)
            throw new InvalidDataException("request body missing a valid Content-Length within the limit");
        var body = new byte[length];
        var copied = Math.Min(length, leftover.Length);
        leftover[..copied].CopyTo(body);
        await stream.ReadExactlyAsync(body.AsMemory(copied), cancellationToken).ConfigureAwait(false);
        return body;
    }
}
