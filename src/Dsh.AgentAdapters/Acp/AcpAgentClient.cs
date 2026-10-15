using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Dsh.Transport;

namespace Dsh.AgentAdapters.Acp;

/**
 * Client side of ACP over a pair of streams (an agent process's stdout and stdin, or in-process pipes).
 * Session updates are raised as events in arrival order; permission requests become AcpPermissionRequest objects
 * that stay pending until a caller answers them, the turn is cancelled, or the agent goes away.
 */
public sealed class AcpAgentClient : IAsyncDisposable
{
    private readonly NdjsonRpcConnection _connection;
    private readonly ConcurrentDictionary<string, AcpPermissionRequest> _pendingPermissions = new(StringComparer.Ordinal);
    private readonly Channel<AcpPermissionRequest> _permissionQueue = Channel.CreateUnbounded<AcpPermissionRequest>();

    public AcpAgentClient(Stream fromAgent, Stream toAgent)
    {
        _connection = new NdjsonRpcConnection(fromAgent, toAgent);
        _connection.NotificationReceived += OnNotification;
        _connection.RequestHandler = OnRequestAsync;
        _connection.Start();
        _ = ReleaseOnCloseAsync();
    }

    public event Action<AcpSessionUpdate>? SessionUpdated;

    /** New permission requests in arrival order; skip entries that are already resolved (cancelled before being read). */
    public ChannelReader<AcpPermissionRequest> PermissionRequests => _permissionQueue.Reader;

    /** Snapshot of the permission requests still waiting for an answer. */
    public IReadOnlyList<AcpPermissionRequest> PendingPermissions => _pendingPermissions.Values.ToList();

    /** Completes when the agent's output ends; the result is the read failure, or null for a clean end. */
    public Task<Exception?> Closed => _connection.Closed;

    public async Task<AcpInitializeResponse> InitializeAsync(AcpImplementationInfo? clientInfo = null, CancellationToken cancellationToken = default)
    {
        var request = new AcpInitializeRequest(AcpProtocol.Version, AcpClientCapabilities.None, clientInfo);
        return await CallAsync(AcpProtocol.Initialize, request, AcpJsonContext.Default.AcpInitializeRequest, AcpJsonContext.Default.AcpInitializeResponse, cancellationToken);
    }

    public Task<AcpSessionResponse> NewSessionAsync(string cwd, CancellationToken cancellationToken = default)
        => SessionAsync(AcpProtocol.SessionNew, new AcpSessionRequest(Path.GetFullPath(cwd), []), cancellationToken);

    /** Needs AcpInitializeResponse.CanLoadSession; the agent replays the history as session updates before this completes. */
    public Task<AcpSessionResponse> LoadSessionAsync(string sessionId, string cwd, CancellationToken cancellationToken = default)
        => SessionAsync(AcpProtocol.SessionLoad, new AcpSessionRequest(Path.GetFullPath(cwd), [], sessionId), cancellationToken);

    /** Needs AcpInitializeResponse.CanResumeSession; restores the agent's context without replaying history. */
    public Task<AcpSessionResponse> ResumeSessionAsync(string sessionId, string cwd, CancellationToken cancellationToken = default)
        => SessionAsync(AcpProtocol.SessionResume, new AcpSessionRequest(Path.GetFullPath(cwd), [], sessionId), cancellationToken);

    /** Sends one user turn and waits for it to end; returns the stop reason (AcpProtocol.Stop*). */
    public async Task<string> PromptAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var request = new AcpPromptRequest(sessionId, [AcpTextContent.Of(text)]);
        var response = await CallAsync(AcpProtocol.SessionPrompt, request, AcpJsonContext.Default.AcpPromptRequest, AcpJsonContext.Default.AcpPromptResponse, cancellationToken);
        return response.StopReason;
    }

    /** Asks the agent to stop the running turn and answers its pending permission requests with "cancelled", as the protocol requires. */
    public async Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var parameters = JsonSerializer.SerializeToElement(new AcpSessionIdParams(sessionId), AcpJsonContext.Default.AcpSessionIdParams);
        await _connection.NotifyAsync(AcpProtocol.SessionCancel, parameters, cancellationToken);
        foreach (var permission in _pendingPermissions.Values.Where(permission => permission.SessionId == sessionId))
            permission.TryCancel();
    }

    private async Task<AcpSessionResponse> SessionAsync(string method, AcpSessionRequest request, CancellationToken cancellationToken)
    {
        var response = await CallAsync(method, request, AcpJsonContext.Default.AcpSessionRequest, AcpJsonContext.Default.AcpSessionResponse, cancellationToken);
        return response.SessionId is null ? response with { SessionId = request.SessionId } : response;
    }

    private async Task<TResponse> CallAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        var result = await _connection.RequestAsync(method, JsonSerializer.SerializeToElement(request, requestType), cancellationToken);
        return result is { } element && element.Deserialize(responseType) is { } response
            ? response
            : throw new JsonRpcException(JsonRpcPeer.InternalError, $"{method}: the agent returned an empty result");
    }

    private void OnNotification(JsonRpcMessage message)
    {
        if (message.Method != AcpProtocol.SessionUpdate || message.Params is not { ValueKind: JsonValueKind.Object } parameters)
            return;
        var sessionId = AcpSessionUpdate.StringOf(parameters, "sessionId");
        if (sessionId is null || !parameters.TryGetProperty("update", out var update))
            return;
        var kind = AcpSessionUpdate.StringOf(update, "sessionUpdate") ?? "";
        SessionUpdated?.Invoke(new AcpSessionUpdate(sessionId, kind, update.Clone()));
    }

    /** Only permission requests are served (file system and terminal capabilities are advertised as off); others get method-not-found. */
    private async Task<JsonElement?> OnRequestAsync(JsonRpcMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != AcpProtocol.RequestPermission || request.Params is not { } parameters)
            return null;
        var decoded = parameters.Deserialize(AcpJsonContext.Default.AcpPermissionParams)
                      ?? throw new JsonRpcException(JsonRpcPeer.InternalError, "empty permission request");
        var permission = new AcpPermissionRequest(decoded.SessionId, AcpPermissionRequest.ToolCallOf(decoded.ToolCall), decoded.Options);
        _pendingPermissions[permission.RequestId] = permission;
        _permissionQueue.Writer.TryWrite(permission);
        try
        {
            await using var registration = cancellationToken.Register(() => permission.TryCancel());
            var outcome = await permission.Outcome;
            return JsonSerializer.SerializeToElement(new AcpPermissionResponse(outcome), AcpJsonContext.Default.AcpPermissionResponse);
        }
        finally
        {
            _pendingPermissions.TryRemove(permission.RequestId, out _);
        }
    }

    /** Once the agent is gone nobody can act on its questions: cancel them so waiters and pending lists are released. */
    private async Task ReleaseOnCloseAsync()
    {
        await _connection.Closed;
        foreach (var permission in _pendingPermissions.Values)
            permission.TryCancel();
        _permissionQueue.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
