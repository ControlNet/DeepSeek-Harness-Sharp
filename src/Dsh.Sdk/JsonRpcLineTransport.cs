using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Dsh.Llm;
using Dsh.Transport;

namespace Dsh.Sdk;

public delegate Task<object?> JsonRpcRequestHandler(string method, JsonElement? parameters);
public delegate void JsonRpcNotificationHandler(string method, JsonElement? parameters);

public interface IJsonRpcPeer
{
    Task<object?> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
    void Notify(string method, object? parameters = null);
}

public sealed class JsonRpcLineTransport : IJsonRpcPeer, IAsyncDisposable
{
    /** JSON-RPC standard error code: the received line is not a valid request object. */
    private const int InvalidRequest = -32600;

    /** Characters requested from the input per read; lines are cut out of these chunks so that no line is buffered beyond the limit. */
    private const int ReadChunkChars = 4096;

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly int _maxLineBytes;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _pending = new();
    private readonly Lock _writeLock = new();
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _readCancellation = new();
    private readonly bool _ownsIo;
    private Task? _readLoop;
    private Exception? _terminalError;
    private bool _stopped;
    private int _disposed;

    /** maxLineBytes bounds one incoming line in UTF-8 bytes; a longer line gets an error reply and closes the transport. */
    public JsonRpcLineTransport(TextReader input, TextWriter output, int maxLineBytes = LineProtocolLimits.MaxLineBytes)
    {
        _input = input;
        _output = output;
        _maxLineBytes = maxLineBytes;
        ConnectionClosed = _readCancellation.Token;
    }

    /** Stream endpoints are owned by this transport; text endpoints retain their existing caller-owned lifetime. */
    public JsonRpcLineTransport(Stream input, Stream output, int maxLineBytes = LineProtocolLimits.MaxLineBytes)
        : this(new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false),
            new StreamWriter(output, new UTF8Encoding(false)) { NewLine = "\n" }, maxLineBytes)
    {
        _ownsIo = true;
    }

    public JsonRpcRequestHandler? RequestHandler { get; set; }
    public JsonRpcNotificationHandler? NotificationHandler { get; set; }

    /** Cancels incoming work when input ends, output fails, or the transport is stopped. */
    public CancellationToken ConnectionClosed { get; }

    /** The original read/write failure, or null for EOF or an explicit stop. */
    public Task<Exception?> Closed => _closed.Task;

    public void Start()
    {
        lock (_writeLock)
        {
            if (_readLoop is not null)
                return;
            if (_stopped)
                throw _terminalError!;
            _readLoop = Task.Run(() => ReadLoopAsync(_readCancellation.Token));
        }
    }

    public Task WhenClosedAsync() => _closed.Task;

    public async Task StopAsync()
    {
        Complete(null);
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsIo)
        {
            // Closing owned streams also releases readers whose underlying pipe ignores cancellation.
            _input.Dispose();
            _output.Dispose();
        }
        if (_readLoop is { } loop)
            await loop.ConfigureAwait(false);
    }

    public async Task<object?> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = $"req_{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetCanceled(cancellationToken);
        });
        try
        {
            WriteFrame(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object?>(),
            }, cancellationToken: cancellationToken);
            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            // A failed write closes all waiters, including this completion whose task was not yet awaited.
            _ = completion.Task.Exception;
            throw;
        }
    }

    public void Notify(string method, object? parameters = null)
    {
        var frame = parameters is null
            ? new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method }
            : new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
        WriteFrame(frame);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var chunk = new char[ReadChunkChars];
        var pending = new StringBuilder();
        var pendingBytes = 0;
        Exception? failure = null;
        try
        {
            int read;
            while ((read = await _input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                pendingBytes = DispatchLines(chunk.AsSpan(0, read), pending, pendingBytes);
                if (pendingBytes > _maxLineBytes)
                {
                    WriteError(null, InvalidRequest, $"request line exceeds {_maxLineBytes} bytes");
                    failure = new JsonException($"request line exceeds {_maxLineBytes} bytes");
                    return;
                }
            }
            if (pending.Length > 0)
                HandleLine(pending.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            Complete(failure);
        }
    }

    /**
     * Hands every complete line of the chunk to HandleLine and keeps the unterminated tail in pending.
     * Returns the tail's UTF-8 size, which exceeds the limit as soon as the line being read does; the tail is then left incomplete.
     */
    private int DispatchLines(ReadOnlySpan<char> chunk, StringBuilder pending, int pendingBytes)
    {
        while (true)
        {
            var newline = chunk.IndexOf('\n');
            var part = newline < 0 ? chunk : chunk[..newline];
            pendingBytes += Encoding.UTF8.GetByteCount(part);
            if (pendingBytes > _maxLineBytes)
                return pendingBytes;
            pending.Append(part);
            if (newline < 0)
                return pendingBytes;
            HandleLine(pending.ToString().TrimEnd('\r'));
            pending.Clear();
            pendingBytes = 0;
            chunk = chunk[(newline + 1)..];
        }
    }

    private void HandleLine(string line)
    {
        if (line.Length == 0)
            return;
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("JSON-RPC message must be an object");
        if (!root.TryGetProperty("method", out var methodElement))
        {
            if (!root.TryGetProperty("id", out var responseId)
                || (!root.TryGetProperty("result", out _) && !root.TryGetProperty("error", out _)))
                throw new JsonException("JSON-RPC response must contain an id and result or error");
            HandleIncomingResponse(responseId, root);
            return;
        }
        if (methodElement.ValueKind != JsonValueKind.String)
            throw new JsonException("JSON-RPC method must be a string");
        var method = methodElement.GetString()!;
        JsonElement? parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : null;
        if (root.TryGetProperty("id", out var requestId))
        {
            // Only the handler's asynchronous operation is detached; notifications and responses retain wire order.
            _ = HandleIncomingRequestAsync(requestId.Clone(), method, parameters);
            return;
        }
        NotificationHandler?.Invoke(method, parameters);
    }

    private async Task HandleIncomingRequestAsync(JsonElement id, string method, JsonElement? parameters)
    {
        try
        {
            var handler = RequestHandler ?? throw new JsonRpcResponseError(-32601, $"method not found: {method}");
            var result = await handler(method, parameters).ConfigureAwait(false);
            WriteFrame(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result,
            }, allowClosed: true);
        }
        catch (Exception error)
        {
            try
            {
                var code = error is JsonRpcResponseError response ? response.Code ?? -32603 : -32603;
                WriteError(id, code, error.Message);
            }
            catch (Exception writeError)
            {
                Complete(writeError);
            }
        }
    }

    private void HandleIncomingResponse(JsonElement id, JsonElement frame)
    {
        var idText = id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
        if (idText is null || !_pending.TryRemove(idText, out var pending))
            return;
        if (frame.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            var code = errorElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number
                ? codeElement.GetInt32()
                : (int?)null;
            var message = errorElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()!
                : "JSON-RPC error";
            pending.TrySetException(new JsonRpcResponseError(code, message,
                errorElement.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : null));
            return;
        }
        pending.TrySetResult(frame.TryGetProperty("result", out var resultElement) ? resultElement.Clone() : null);
    }

    private void WriteFrame(IReadOnlyDictionary<string, object?> frame, bool allowClosed = false, CancellationToken cancellationToken = default)
    {
        var line = DshJson.Serialize(frame);
        lock (_writeLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((_stopped && !allowClosed) || Volatile.Read(ref _disposed) != 0)
                throw _terminalError ?? new JsonRpcTransportClosedException("JSON-RPC transport closed");
            try
            {
                // Cancellation only applies before a frame starts; never leave a partial NDJSON line behind.
                _output.WriteLine(line);
                _output.Flush();
            }
            catch (Exception error)
            {
                Complete(error);
                throw;
            }
        }
    }

    /** A null id answers a line whose request id could not be read. */
    private void WriteError(JsonElement? id, int code, string message)
    {
        WriteFrame(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.Clone(),
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message,
            },
        }, allowClosed: true);
    }

    private void Complete(Exception? failure)
    {
        lock (_writeLock)
        {
            if (_stopped)
                return;
            _stopped = true;
            _terminalError = failure ?? new JsonRpcTransportClosedException("JSON-RPC input closed");
            FailPending(_terminalError);
        }
        _readCancellation.Cancel();
        _closed.TrySetResult(failure);
    }

    private void FailPending(Exception error)
    {
        foreach (var pending in _pending)
        {
            if (_pending.TryRemove(pending.Key, out var completion))
                completion.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _readCancellation.Dispose();
    }
}

public sealed class JsonRpcResponseError : Exception
{
    public JsonRpcResponseError(int? code, string message, JsonElement? data = null)
        : base(message)
    {
        Code = code;
        DataValue = data;
    }

    public int? Code { get; }
    public JsonElement? DataValue { get; }
}

public sealed class JsonRpcTransportClosedException : IOException
{
    public JsonRpcTransportClosedException(string message) : base(message)
    {
    }
}
