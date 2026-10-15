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
    private string? _error;

    public McpServerConfig Config { get; } = config;

    public long Version => Volatile.Read(ref _version);

    public bool Connected => Volatile.Read(ref _live) is not null;

    public int ToolCount => Volatile.Read(ref _live)?.Tools.Count ?? 0;

    public bool SupportsResources => Volatile.Read(ref _live)?.Client.ServerCapabilities.Resources is not null;

    public long ResourceListChanges => Interlocked.Read(ref _resourceListChanges);

    /** 最近一次成功列举之后又收到过 list_changed 通知。 */
    public bool ResourceListStale => ResourceListChanges > Interlocked.Read(ref _resourceListSeen);

    public McpServerStatus Status => new(Config.Name, Config.Transport, Connected, ToolCount, _error, SupportsResources, ResourceListChanges);

    public async Task<IList<McpClientTool>> ConnectAsync(CancellationToken signal)
    {
        var options = new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                NotificationHandlers = [new(NotificationMethods.ResourceListChangedNotification, OnResourceListChanged)],
            },
        };
        var client = await McpClient.CreateAsync(transportFactory(), options, NullLoggerFactory.Instance, signal);
        var tools = await client.ListToolsAsync(cancellationToken: signal);
        var listen = new CancellationTokenSource();
        if (client.ServerCapabilities.Resources?.ListChanged == true
            && string.CompareOrdinal(client.NegotiatedProtocolVersion, ListenProtocolVersion) >= 0)
            _ = ListenForResourceListChangesAsync(client, listen.Token);
        Volatile.Write(ref _live, new Live(client, tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal), listen));
        _error = null;
        Interlocked.Increment(ref _version);
        return tools;
    }

    public void MarkError(string message) => _error = message;

    public bool TryGetTool(string toolName, out McpClientTool? tool, out long version)
    {
        var live = Volatile.Read(ref _live);
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

    /** 列举请求发出前取号, 成功后记为已读到该号为止的通知, 使请求期间到达的通知仍算未读。 */
    public async ValueTask<T> CallListingAsync<T>(Func<McpClient, ValueTask<T>> call, CancellationToken signal)
    {
        var observed = ResourceListChanges;
        var result = await CallResourcesAsync(call, signal);
        long seen;
        do
            seen = Interlocked.Read(ref _resourceListSeen);
        while (seen < observed && Interlocked.CompareExchange(ref _resourceListSeen, observed, seen) != seen);
        return result;
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
        var client = Volatile.Read(ref _live)?.Client
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

    private static async Task ListenForResourceListChangesAsync(McpClient client, CancellationToken signal)
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
            await client.SendRequestAsync(request, signal);
        }
        catch (Exception)
        {
            // 订阅流随会话关闭或取消而结束; 订阅失败只意味着收不到资源列表变更通知。
        }
    }

    private static async Task CloseAsync(Live? live)
    {
        if (live is null)
            return;
        await live.Listen.CancelAsync();
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

    private sealed record Live(McpClient Client, IReadOnlyDictionary<string, McpClientTool> Tools, CancellationTokenSource Listen);
}
