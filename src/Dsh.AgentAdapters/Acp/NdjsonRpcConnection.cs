using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dsh.Transport;

namespace Dsh.AgentAdapters.Acp;

/**
 * JSON-RPC 2.0 over newline-delimited JSON (one message per line), the stdio framing used by ACP agents.
 * Reuses the Dsh.Transport message model; unlike the length-framed JsonRpcPeer, incoming requests run concurrently,
 * because an ACP permission request waits for a human while session/update notifications keep streaming.
 * Notifications are dispatched on the read loop in arrival order, before the response that follows them completes.
 */
public sealed class NdjsonRpcConnection : IAsyncDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcMessage>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private int _disposed;
    private Task? _readLoop;

    public NdjsonRpcConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /** Handles an incoming request: return the result, return null for method-not-found, or throw JsonRpcException for a specific error. */
    public Func<JsonRpcMessage, CancellationToken, Task<JsonElement?>>? RequestHandler { get; set; }

    public event Action<JsonRpcMessage>? NotificationReceived;

    /** Completes when the input ends; the result is the failure, or null for a clean end of stream. */
    public Task<Exception?> Closed => _closed.Task;

    public void Start() => _readLoop ??= Task.Run(() => ReadLoopAsync(_stop.Token));

    public async Task<JsonElement?> RequestAsync(string method, JsonElement? parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var key = id.ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = completion;
        try
        {
            var message = new JsonRpcMessage { Id = JsonSerializer.SerializeToElement(id, JsonRpcJsonContext.Default.Int64), Method = method, Params = parameters };
            await SendAsync(message, cancellationToken);
            await using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            var response = await completion.Task;
            return response.Error is { } error ? throw new JsonRpcException(error.Code, error.Message, error.Data) : response.Result;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public Task NotifyAsync(string method, JsonElement? parameters, CancellationToken cancellationToken = default)
        => SendAsync(new JsonRpcMessage { Method = method, Params = parameters }, cancellationToken);

    private async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonRpcJsonContext.Default.JsonRpcMessage);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(payload, cancellationToken);
            await _output.WriteAsync(NewLine, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            using var reader = new StreamReader(_input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                Dispatch(line);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            failure = error;
        }
        FailPending(failure ?? new IOException("the agent closed its output"));
        _closed.TrySetResult(failure);
    }

    /** Invalid JSON ends the connection, failing pending calls instead of silently waiting for a discarded response. */
    private void Dispatch(string line)
    {
        var message = JsonSerializer.Deserialize(line, JsonRpcJsonContext.Default.JsonRpcMessage)
            ?? throw new JsonException("the agent sent an empty JSON-RPC message");
        // A message without a method is a response even when its result is JSON null (which deserializes to a missing Result).
        if (message.Method is null)
        {
            if (message.IdKey is { } key && _pending.TryGetValue(key, out var completion))
                completion.TrySetResult(message);
        }
        else if (message.IsNotification)
            NotificationReceived?.Invoke(message);
        else if (message.IsRequest)
            _ = RespondAsync(message);
    }

    private async Task RespondAsync(JsonRpcMessage request)
    {
        var response = new JsonRpcMessage { Id = request.Id };
        try
        {
            var handler = RequestHandler ?? throw MethodNotFound(request);
            response.Result = await handler(request, _stop.Token) ?? throw MethodNotFound(request);
        }
        catch (JsonRpcException error)
        {
            response.Error = new JsonRpcError { Code = error.Code, Message = error.Message, Data = error.ErrorData };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            response.Error = new JsonRpcError { Code = JsonRpcPeer.InternalError, Message = error.Message };
        }
        await SendQuietlyAsync(response);
    }

    /** A response to a closed agent is pointless; losing it must not surface as an unobserved task exception. */
    private async Task SendQuietlyAsync(JsonRpcMessage response)
    {
        try
        {
            await SendAsync(response, _stop.Token);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static JsonRpcException MethodNotFound(JsonRpcMessage request)
        => new(JsonRpcPeer.MethodNotFound, $"method not found: {request.Method}");

    private void FailPending(Exception failure)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
                completion.TrySetException(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync();
        // Closing the streams unblocks a pending read that ignores the token (pipes do not always honour cancellation).
        await _output.DisposeAsync();
        await _input.DisposeAsync();
        if (_readLoop is { } loop)
            await loop;
        _writeGate.Dispose();
        _stop.Dispose();
    }
}
