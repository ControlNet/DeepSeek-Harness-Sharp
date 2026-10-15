using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dsh.WebBridge;

namespace Dsh.Tests;

/** What a browser needs to reach a running bridge: its loopback port and its web access token, plus logging in and opening WebSockets like the page does. */
internal sealed record BridgeAccess(int Port, string Token)
{
    public string Origin => $"http://127.0.0.1:{Port}";

    public Uri WebSocketUri => new($"ws://127.0.0.1:{Port}{WebBridgeServer.WebSocketPath}");

    public Uri Url(string path) => new($"{Origin}{path}");

    /** The WebSocket endpoint that targets one machine of the bridge's fleet. */
    public Uri MachineWebSocketUri(string machine)
        => new($"{WebSocketUri}?{WebBridgeServer.MachineParameter}={Uri.EscapeDataString(machine)}");

    public static HttpClient CreateHttpClient(CookieContainer cookies)
        => new(new SocketsHttpHandler { CookieContainer = cookies, UseCookies = true });

    /** ClientWebSocket sends no Origin by default, and the bridge refuses upgrades without a same-origin one. */
    public static ClientWebSocket CreateWebSocket(CookieContainer cookies, string origin)
    {
        var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        socket.Options.SetRequestHeader("Origin", origin);
        socket.Options.CollectHttpResponseDetails = true;
        return socket;
    }

    public async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string token, string origin, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(WebBridgeServer.LoginPath))
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]),
        };
        request.Headers.Add("Origin", origin);
        return await client.SendAsync(request, cancellationToken);
    }

    /** Logs in like the page does and returns the cookie jar holding the session cookie. */
    public async Task<CookieContainer> LoginAsync(CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        using var client = CreateHttpClient(cookies);
        using var response = await PostLoginAsync(client, Token, Origin, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return cookies;
    }

    /** Polls /api/machines until the machine states (name to state) satisfy the condition; returns that body. */
    public async Task<string> WaitForMachinesAsync(Func<Dictionary<string, string>, bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(await LoginAsync(cancellationToken));
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var body = await client.GetStringAsync(Url(WebBridgeServer.MachinesPath), cancellationToken);
            var states = JsonDocument.Parse(body).RootElement.EnumerateArray()
                .ToDictionary(machine => machine.GetProperty("Machine").GetString()!, machine => machine.GetProperty("State").GetString()!);
            if (condition(states))
                return body;
            Assert.True(DateTime.UtcNow < deadline, $"machines never reached the expected states: {body}");
            await Task.Delay(100, cancellationToken);
        }
    }

    public async Task<ClientWebSocket> ConnectWebSocketAsync(CookieContainer cookies, string origin, CancellationToken cancellationToken, string? machine = null)
    {
        var socket = CreateWebSocket(cookies, origin);
        await socket.ConnectAsync(machine is null ? WebSocketUri : MachineWebSocketUri(machine), cancellationToken);
        return socket;
    }
}

/** JSON-RPC over a raw WebSocket, one message per JSON-RPC message, as the bridge relays it. */
internal static class WebSocketRpc
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task SendAsync(ClientWebSocket socket, int id, string method, string? parameters)
    {
        var message = parameters is null
            ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""";
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, Ct);
    }

    public static async Task<JsonElement> RequestAsync(ClientWebSocket socket, int id, string method, string? parameters)
    {
        await SendAsync(socket, id, method, parameters);
        var response = await ReceiveUntilAsync(socket, message => message.TryGetProperty("id", out var reply) && reply.ValueKind == JsonValueKind.Number && reply.GetInt32() == id);
        Assert.False(response.TryGetProperty("error", out var error), error.ToString());
        return response.GetProperty("result");
    }

    /** Reads whole WebSocket text messages, each one JSON message, until one matches. */
    public static async Task<JsonElement> ReceiveUntilAsync(ClientWebSocket socket, Func<JsonElement, bool> match)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), Ct);
                Assert.Equal(WebSocketMessageType.Text, received.MessageType);
                message.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);

            var element = JsonDocument.Parse(message.ToArray()).RootElement.Clone();
            if (match(element))
                return element;
        }
    }

    public static async Task WaitForAsync(Func<bool> condition, int attempts = 500)
    {
        for (var attempt = 0; attempt < attempts && !condition(); attempt++)
            await Task.Delay(10, Ct);
        Assert.True(condition(), "condition not met in time");
    }
}
