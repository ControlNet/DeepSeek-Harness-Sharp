using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A;
using Dsh.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dsh.A2A;

/**
 * 自备 A2A 宿主: 核心 A2A SDK 的 A2AServer 承载协议语义, 这里只做 HTTP 路由(well-known card + JSON-RPC)
 * 与 SSE 帧。刻意不引 A2A.AspNetCore, 以免宿主整体依赖 ASP.NET Core 共享框架。
 */
public sealed class A2aHost : IDisposable
{
    private const string CardPath = "/.well-known/agent-card.json";
    private static readonly JsonSerializerOptions Json = A2AJsonUtilities.DefaultOptions;

    private const string BearerPrefix = "Bearer ";

    private readonly A2AServer _server;
    private AgentCard? _card;
    private readonly A2aHostOptions _options;
    private readonly string _host;
    private string _authority;
    private readonly TcpListener _listener;
    private readonly Dictionary<TcpClient, Task> _clients = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[]? _authToken;
    private Task? _loop;
    private int _disposed;

    public A2aHost(DshAgentHandler handler, A2aHostOptions options)
    {
        _options = options;
        var port = options.Port > 0 ? options.Port : 0;
        var host = string.IsNullOrWhiteSpace(options.Host) ? "127.0.0.1" : options.Host;
        // The endpoint runs agent turns with the user's tools, so an unauthenticated listener must stay unreachable from other machines.
        if (string.IsNullOrEmpty(options.AuthToken) && !IsLoopbackHost(host))
            throw new InvalidOperationException($"a2a.host \"{host}\" is not a loopback address; set a2a.authToken before exposing the A2A server");
        _authToken = string.IsNullOrEmpty(options.AuthToken) ? null : Encoding.UTF8.GetBytes(options.AuthToken);
        _host = host;
        _authority = $"{host}:{port}";
        Endpoint = $"http://{_authority}";
        _listener = new TcpListener(BindAddress(host), port);
        _server = new A2AServer(handler, new InMemoryTaskStore(), new ChannelEventNotifier(), NullLogger<A2AServer>.Instance);
    }

    /** The actual bound endpoint is published by Start, after the socket owns its assigned port. */
    public string Endpoint { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_loop is not null)
            return;
        _listener.Start();
        _authority = $"{_host}:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        Endpoint = $"http://{_authority}";
        _card = BuildCard(_options.PublicUrl ?? Endpoint, _options);
        _loop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _listener.Stop();
        _loop?.GetAwaiter().GetResult();
        TcpClient[] clients;
        Task[] handlers;
        lock (_clients)
        {
            clients = [.. _clients.Keys];
            handlers = [.. _clients.Values];
        }
        foreach (var client in clients)
            client.Dispose();
        Task.WhenAll(handlers).GetAwaiter().GetResult();
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stop.Dispose();
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
            var stream = client.GetStream();
            lock (_clients)
            {
                var handler = HandleClientAsync(client, stream);
                if (!handler.IsCompleted)
                    _clients.Add(client, handler);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, Stream stream)
    {
        using (client)
        {
            try
            {
                var request = await HttpRequestReader.ReadAsync(stream, _stop.Token, int.MaxValue, allowChunked: true).ConfigureAwait(false);
                if (request is null)
                    return;
                // Preserve host routing so a loopback socket cannot accept a DNS-rebound browser origin.
                var host = request.Header("Host");
                if (_host is not ("+" or "*") && !string.Equals(host, _authority, StringComparison.OrdinalIgnoreCase)
                    && !(_listener.LocalEndpoint is IPEndPoint { Port: 80 } && string.Equals(host, _host, StringComparison.OrdinalIgnoreCase)))
                {
                    await HttpResponse.WriteTextAsync(stream, HttpStatus.Forbidden, "unexpected Host header", _stop.Token).ConfigureAwait(false);
                    return;
                }
                if (!IsAuthorized(request))
                {
                    await HttpResponse.WriteAsync(stream, HttpStatus.Unauthorized, ReadOnlyMemory<byte>.Empty,
                        HttpResponse.PlainText, "WWW-Authenticate: Bearer\r\n", _stop.Token).ConfigureAwait(false);
                    return;
                }
                if (request.Method == "GET" && request.Path.EndsWith(CardPath, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(stream, _card!).ConfigureAwait(false);
                    return;
                }
                if (request.Method == "POST")
                {
                    JsonRpcRequest? rpc;
                    try
                    {
                        rpc = JsonSerializer.Deserialize<JsonRpcRequest>(request.Body, Json);
                    }
                    catch (JsonException)
                    {
                        await WriteJsonAsync(stream, JsonRpcResponse.ParseErrorResponse(default)).ConfigureAwait(false);
                        return;
                    }
                    if (rpc is null)
                    {
                        await WriteJsonAsync(stream, JsonRpcResponse.ParseErrorResponse(default)).ConfigureAwait(false);
                        return;
                    }
                    await DispatchAsync(stream, rpc, _stop.Token).ConfigureAwait(false);
                    return;
                }
                await HttpResponse.WriteTextAsync(stream, HttpStatus.NotFound, "", _stop.Token).ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                await TryFailAsync(stream, HttpStatus.BadRequest, error.Message).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
            catch (Exception)
            {
                await TryFailAsync(stream, HttpStatus.InternalServerError, "").ConfigureAwait(false);
            }
            finally
            {
                client.Dispose();
                lock (_clients)
                    _clients.Remove(client);
            }
        }
    }

    private async Task DispatchAsync(Stream stream, JsonRpcRequest rpc, CancellationToken cancellationToken)
    {
        try
        {
            switch (rpc.Method)
            {
                case A2AMethods.SendMessage:
                    await WriteJsonAsync(stream, JsonRpcResponse.CreateJsonRpcResponse(rpc.Id,
                        await _server.SendMessageAsync(Require<SendMessageRequest>(rpc), cancellationToken)));
                    return;
                case A2AMethods.GetTask:
                    await WriteJsonAsync(stream, JsonRpcResponse.CreateJsonRpcResponse(rpc.Id,
                        await _server.GetTaskAsync(Require<GetTaskRequest>(rpc), cancellationToken)));
                    return;
                case A2AMethods.ListTasks:
                    await WriteJsonAsync(stream, JsonRpcResponse.CreateJsonRpcResponse(rpc.Id,
                        await _server.ListTasksAsync(Require<ListTasksRequest>(rpc), cancellationToken)));
                    return;
                case A2AMethods.CancelTask:
                    await WriteJsonAsync(stream, JsonRpcResponse.CreateJsonRpcResponse(rpc.Id,
                        await _server.CancelTaskAsync(Require<CancelTaskRequest>(rpc), cancellationToken)));
                    return;
                case A2AMethods.SendStreamingMessage:
                    await WriteStreamAsync(stream, rpc.Id, _server.SendStreamingMessageAsync(Require<SendMessageRequest>(rpc), cancellationToken), cancellationToken);
                    return;
                case A2AMethods.SubscribeToTask:
                    await WriteStreamAsync(stream, rpc.Id, _server.SubscribeToTaskAsync(Require<SubscribeToTaskRequest>(rpc), cancellationToken), cancellationToken);
                    return;
                default:
                    await WriteJsonAsync(stream, JsonRpcResponse.MethodNotFoundResponse(rpc.Id, $"unknown method: {rpc.Method}"));
                    return;
            }
        }
        catch (A2AException error)
        {
            await WriteJsonAsync(stream, JsonRpcResponse.CreateJsonRpcErrorResponse(rpc.Id, error));
        }
        catch (Exception error) when (error is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await WriteJsonAsync(stream, JsonRpcResponse.InternalErrorResponse(rpc.Id, error.Message));
        }
    }

    private static T Require<T>(JsonRpcRequest rpc)
    {
        if (rpc.Params is not { } parameters)
            throw new A2AException($"method {rpc.Method} requires params", A2AErrorCode.InvalidParams);
        return parameters.Deserialize<T>(Json)
            ?? throw new A2AException($"invalid params for {rpc.Method}", A2AErrorCode.InvalidParams);
    }

    private static async Task WriteStreamAsync(Stream stream, JsonRpcId id, IAsyncEnumerable<StreamResponse> events, CancellationToken cancellationToken)
    {
        await HttpResponse.WriteHeadAsync(stream, HttpStatus.Ok, "text/event-stream", null, "", cancellationToken).ConfigureAwait(false);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        await foreach (var streamEvent in events.WithCancellation(cancellationToken))
        {
            var frame = JsonRpcResponse.CreateJsonRpcResponse(id, streamEvent);
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(frame, Json)}\n\n");
            await writer.FlushAsync();
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task WriteJsonAsync(Stream stream, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        return HttpResponse.WriteAsync(stream, HttpStatus.Ok, bytes, HttpResponse.Json, "", _stop.Token);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (_authToken is null)
            return true;
        var header = request.Header("Authorization");
        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var presented = Encoding.UTF8.GetBytes(header[BearerPrefix.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, _authToken);
    }

    /** Wildcards and unspecified addresses bind every interface, so only literal loopback names count. */
    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    private async Task TryFailAsync(Stream stream, HttpStatus status, string message)
    {
        try
        {
            await HttpResponse.WriteTextAsync(stream, status, message, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static IPAddress BindAddress(string host)
    {
        if (host is "+" or "*")
            return IPAddress.Any;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        return IPAddress.TryParse(host.Trim('[', ']'), out var address) ? address : Dns.GetHostAddresses(host)[0];
    }

    private static AgentCard BuildCard(string url, A2aHostOptions options)
    {
        var skill = options.Skill;
        return new AgentCard
        {
            Name = "deepseek-harness",
            Description = "DeepSeek Harness agent exposed over the A2A protocol",
            Version = typeof(A2aHost).Assembly.GetName().Version?.ToString() ?? "0.0.1",
            SupportedInterfaces = [new AgentInterface { Url = url, ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
            Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = false },
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
            Skills = [new AgentSkill { Id = skill.Id, Name = skill.Name, Description = skill.Description, Tags = [.. skill.EffectiveTags] }],
        };
    }
}
