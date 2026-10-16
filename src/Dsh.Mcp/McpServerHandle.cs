using System.Text.Json;
using Dsh.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dsh.Mcp;

/**
 * 一个 MCP server 的可换句柄: 持有当前 McpClient 与工具表, 并可按需重连(重新握手取新 session)。
 * 注册到 ToolRuntime 的 ToolDefinition 绑定本句柄而非某个具体 McpClientTool, 因此重连后无需重注册、agent 视角不失效。
 * 句柄还统计 resources/list_changed 通知, 资源工具据此提示模型重新列举。
 */
internal sealed class McpServerHandle(McpServerConfig config, Func<IClientTransport> transportFactory)
{
    public const string UnavailableCode = "MCP_SERVER_UNAVAILABLE";
    public const string ResourcesUnsupportedCode = "MCP_RESOURCES_UNSUPPORTED";

    /** 自此版本起 server 只向打开 subscriptions/listen 的客户端推送 list_changed。 */
    private const string ListenProtocolVersion = "2026-07-28";

    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private Live? _live;
    private long _version;
    private long _resourceListChanges;
    private long _resourceListSeen;
    private long _resourceTemplatesSeen;
    private string? _error;

    public McpServerConfig Config { get; } = config;

    public long Version => Volatile.Read(ref _version);

    public bool Connected => Volatile.Read(ref _live) is not null;

    public int ToolCount => Volatile.Read(ref _live)?.Tools.Count ?? 0;

    public bool SupportsResources => Volatile.Read(ref _live)?.Client.ServerCapabilities.Resources is not null;

    public long ResourceListChanges => Interlocked.Read(ref _resourceListChanges);

    /** 最近一次成功列举之后又收到过 list_changed 通知。 */
    public bool ResourceListStale => ResourceListChanges > Interlocked.Read(ref _resourceListSeen);

    public bool ResourceTemplatesStale => ResourceListChanges > Interlocked.Read(ref _resourceTemplatesSeen);

    public string? ResourceNotificationError => Volatile.Read(ref _live)?.NotificationError?.Message;

    public McpServerStatus Status => new(Config.Name, Config.Transport, Connected, ToolCount, _error, SupportsResources, ResourceListChanges);

    public async Task<IList<McpClientTool>> ConnectAsync(CancellationToken signal)
    {
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                NotificationHandlers =
                [
                    new(NotificationMethods.ResourceListChangedNotification, OnResourceListChanged),
                    new(NotificationMethods.SubscriptionsAcknowledgedNotification, (notification, _) =>
                    {
                        acknowledged.TrySetResult(notification.Params?["notifications"]?["resourcesListChanged"]?.GetValue<bool>() == true);
                        return ValueTask.CompletedTask;
                    }),
                ],
            },
        };
        var client = await McpClient.CreateAsync(transportFactory(), options, NullLoggerFactory.Instance, signal);
        Live? live = null;
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: signal);
            live = new Live(client, tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal), new CancellationTokenSource());
            Volatile.Write(ref _live, live);
            _error = null;
            if (Interlocked.Increment(ref _version) > 1)
                Interlocked.Increment(ref _resourceListChanges);
            if (client.ServerCapabilities.Resources?.ListChanged == true
                && string.CompareOrdinal(client.NegotiatedProtocolVersion, ListenProtocolVersion) >= 0)
            {
                live.ListenTask = ListenForResourceListChangesAsync(live);
                await Task.WhenAny(acknowledged.Task, live.ListenTask).WaitAsync(options.InitializationTimeout, signal);
                if (acknowledged.Task.IsCompletedSuccessfully && !acknowledged.Task.Result)
                    NotificationStreamEnded(live, new McpNotificationStreamEndedException("MCP server did not grant resource list change notifications"));
            }
            return tools;
        }
        catch
        {
            if (live is not null)
            {
                Interlocked.CompareExchange(ref _live, null, live);
                await CloseAsync(live);
            }
            else
                await client.DisposeAsync();
            throw;
        }
    }

    public void MarkError(string message) => _error = message;

    public bool TryGetTool(string toolName, out McpClientTool? tool, out long version)
    {
        var live = Volatile.Read(ref _live);
        ThrowIfNotificationsEnded(live);
        version = Volatile.Read(ref _version);
        tool = null;
        return live is not null && live.Tools.TryGetValue(toolName, out tool);
    }

    /** 在声明了 resources 能力的当前会话上执行一次资源请求; 陈旧会话失败时重连一次并重试一次。 */
    public async ValueTask<T> CallResourcesAsync<T>(Func<McpClient, ValueTask<T>> call, CancellationToken signal)
        => await McpToolBridge.CallWithReconnectAsync(
            invoke: () => call(ResourcesClient()),
            usedVersion: () => Version,
            reconnect: version => ReconnectIfVersionAsync(version, signal),
            signal: signal);

    /** 只有完整列举才消费对应列表的通知; 单页不代表模型看到了整个列表。请求期间的新通知仍未读。 */
    public async ValueTask<T> CallListingAsync<T>(Func<McpClient, ValueTask<T>> call, bool templates, bool complete, CancellationToken signal)
    {
        var observed = ResourceListChanges;
        var result = await CallResourcesAsync(call, signal);
        if (complete)
        {
            if (templates)
                InterlockedMax(ref _resourceTemplatesSeen, observed);
            else
                InterlockedMax(ref _resourceListSeen, observed);
        }
        return result;
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long seen;
        do
            seen = Interlocked.Read(ref target);
        while (seen < value && Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    // 只在"调用时用的版本仍是最新"时才重连, 使并发调用共享一次重连。
    public async Task ReconnectIfVersionAsync(long usedVersion, CancellationToken signal)
    {
        if (Volatile.Read(ref _version) != usedVersion)
            return;
        await _reconnectGate.WaitAsync(signal);
        try
        {
            if (Volatile.Read(ref _version) != usedVersion)
                return;
            // 已重启的 server 可能拒绝旧 session 的 DELETE, 关闭旧会话只尽力而为。
            await CloseAsync(Interlocked.Exchange(ref _live, null));
            try
            {
                await ConnectAsync(signal);
            }
            catch (Exception error)
            {
                _error = error.Message;
                throw;
            }
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(Interlocked.Exchange(ref _live, null));
        _reconnectGate.Dispose();
    }

    private McpClient ResourcesClient()
    {
        var live = Volatile.Read(ref _live);
        ThrowIfNotificationsEnded(live);
        var client = live?.Client
            ?? throw new HarnessException($"mcp server \"{Config.Name}\" is not connected: {_error ?? "connection closed"}", UnavailableCode);
        return client.ServerCapabilities.Resources is not null
            ? client
            : throw new HarnessException(
                $"mcp server \"{Config.Name}\" does not declare the resources capability, so it has no resources or resource templates; use its own mcp_{McpToolBridge.Sanitize(Config.Name)}_* tools instead",
                ResourcesUnsupportedCode);
    }

    private ValueTask OnResourceListChanged(JsonRpcNotification notification, CancellationToken signal)
    {
        Interlocked.Increment(ref _resourceListChanges);
        return ValueTask.CompletedTask;
    }

    private async Task ListenForResourceListChangesAsync(Live live)
    {
        var request = new JsonRpcRequest
        {
            Method = RequestMethods.SubscriptionsListen,
            Params = JsonSerializer.SerializeToNode(
                new SubscriptionsListenRequestParams
                {
                    Notifications = new SubscriptionsListenNotifications { ResourcesListChanged = true },
                },
                McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(SubscriptionsListenRequestParams))),
        };
        try
        {
            await live.Client.SendRequestAsync(request, live.Listen.Token);
            if (!live.Listen.IsCancellationRequested)
                NotificationStreamEnded(live, new McpNotificationStreamEndedException("MCP resource notification stream ended while the connection is still active"));
        }
        catch (Exception) when (live.Listen.IsCancellationRequested)
        {
            // 本句柄主动关闭会话, 不属于通知丢失。
        }
        catch (Exception error)
        {
            NotificationStreamEnded(live, new McpNotificationStreamEndedException($"MCP resource notification stream failed: {error.Message}", error));
        }
    }

    private void NotificationStreamEnded(Live live, McpNotificationStreamEndedException error)
    {
        live.NotificationError = error;
        if (ReferenceEquals(Volatile.Read(ref _live), live))
        {
            _error = error.Message;
            Interlocked.Increment(ref _resourceListChanges);
        }
    }

    private static void ThrowIfNotificationsEnded(Live? live)
    {
        if (live?.NotificationError is { } error)
            throw error;
    }

    private static async Task CloseAsync(Live? live)
    {
        if (live is null)
            return;
        await live.Listen.CancelAsync();
        if (live.ListenTask is { } listenTask)
            await listenTask;
        live.Listen.Dispose();
        try
        {
            await live.Client.DisposeAsync();
        }
        catch
        {
            // 尽最大努力释放。
        }
    }

    private sealed record Live(McpClient Client, IReadOnlyDictionary<string, McpClientTool> Tools, CancellationTokenSource Listen)
    {
        public Task? ListenTask { get; set; }
        public volatile McpNotificationStreamEndedException? NotificationError;
    }
}

/** 通知流失效使当前 MCP 会话不可再信赖, 下一次调用沿现有单次重连边界恢复。 */
internal sealed class McpNotificationStreamEndedException(string message, Exception? inner = null) : Exception(message, inner);
