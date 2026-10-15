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
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _readCancellation;
    private Task? _readLoop;
    private bool _started;

    /** maxLineBytes bounds one incoming line in UTF-8 bytes; a longer line gets an error reply and closes the transport. */
    public JsonRpcLineTransport(TextReader input, TextWriter output, int maxLineBytes = LineProtocolLimits.MaxLineBytes)
    {
        _input = input;
        _output = output;
        _maxLineBytes = maxLineBytes;
    }

    public JsonRpcRequestHandler? RequestHandler { get; set; }
    public JsonRpcNotificationHandler? NotificationHandler { get; set; }

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _readCancellation = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_readCancellation.Token));
    }

    public Task WhenClosedAsync() => _closed.Task;

    public async Task StopAsync()
    {
        if (!_started)
            return;
        _started = false;
        _readCancellation?.Cancel();
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        FailPending(new JsonRpcTransportClosedException("JSON-RPC transport closed"));
        _readCancellation?.Dispose();
        _readCancellation = null;
    }

    public async Task<object?> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
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
            });
            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
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
        try
        {
            int read;
            while ((read = await _input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                pendingBytes = DispatchLines(chunk.AsSpan(0, read), pending, pendingBytes);
                if (pendingBytes > _maxLineBytes)
                {
                    WriteError(null, InvalidRequest, $"request line exceeds {_maxLineBytes} bytes");
                    return;
                }
            }
            if (pending.Length > 0)
                HandleLine(pending.ToString());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            FailPending(new JsonRpcTransportClosedException($"JSON-RPC input failed: {error.Message}"));
        }
        finally
        {
            FailPending(new JsonRpcTransportClosedException("JSON-RPC input closed"));
            _closed.TrySetResult();
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
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                if (root.TryGetProperty("id", out var responseId))
                    HandleIncomingResponse(responseId, root);
                return;
            }

            var method = methodElement.GetString()!;
            JsonElement? parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : null;
            if (root.TryGetProperty("id", out var requestId))
            {
                _ = HandleIncomingRequestAsync(requestId.Clone(), method, parameters);
                return;
            }
            NotificationHandler?.Invoke(method, parameters);
        }
    }

    private async Task HandleIncomingRequestAsync(JsonElement id, string method, JsonElement? parameters)
    {
        var handler = RequestHandler;
        if (handler is null)
        {
            WriteError(id, -32601, $"method not found: {method}");
            return;
        }
        try
        {
            var result = await handler(method, parameters).ConfigureAwait(false);
            WriteFrame(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.Clone(),
                ["result"] = result,
            });
        }
        catch (Exception error)
        {
            WriteError(id, -32603, error.Message);
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

    private void WriteFrame(IReadOnlyDictionary<string, object?> frame)
    {
        var line = DshJson.Serialize(frame);
        lock (_writeLock)
        {
            _output.WriteLine(line);
            _output.Flush();
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
        });
    }

    private void FailPending(Exception error)
    {
        foreach (var pending in _pending)
        {
            if (_pending.TryRemove(pending.Key, out var completion))
                completion.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
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

public sealed class JsonRpcTransportClosedException : Exception
{
    public JsonRpcTransportClosedException(string message) : base(message)
    {
    }
}
