using System.Text.Json;
using Dsh.Sdk;

namespace Dsh.Acp;

public sealed record AcpSessionUpdate(string SessionId, string Kind, string? Text, JsonElement Raw);

public sealed class AcpClient
{
    private readonly JsonRpcLineTransport _transport;

    public AcpClient(JsonRpcLineTransport transport)
    {
        _transport = transport;
        _transport.NotificationHandler = OnNotification;
    }

    public event Action<AcpSessionUpdate>? SessionUpdate;

    public async Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await _transport.RequestAsync(AcpMethods.Initialize, new Dictionary<string, object?>
        {
            ["protocolVersion"] = AcpMethods.ProtocolVersion,
            ["clientCapabilities"] = new Dictionary<string, object?>
            {
                ["fs"] = new Dictionary<string, object?> { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false,
            },
        }, cancellationToken);
        return (JsonElement)result!;
    }

    public async Task AuthenticateAsync(string token, CancellationToken cancellationToken = default)
        => await _transport.RequestAsync(AcpMethods.Authenticate, new Dictionary<string, object?>
        {
            ["methodId"] = AcpMethods.LocalTokenAuthMethod,
            ["_meta"] = new Dictionary<string, object?> { ["token"] = token },
        }, cancellationToken);

    public async Task<string> NewSessionAsync(string cwd, CancellationToken cancellationToken = default)
    {
        var result = await _transport.RequestAsync(AcpMethods.NewSession, new Dictionary<string, object?>
        {
            ["cwd"] = cwd,
            ["mcpServers"] = Array.Empty<object?>(),
        }, cancellationToken);
        return ((JsonElement)result!).GetProperty("sessionId").GetString()!;
    }

    public async Task<string> PromptAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var result = await _transport.RequestAsync(AcpMethods.Prompt, new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new object?[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } },
        }, cancellationToken);
        var element = (JsonElement)result!;
        return element.TryGetProperty("stopReason", out var stopReason) ? stopReason.GetString() ?? "" : "";
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => await _transport.RequestAsync(AcpMethods.CloseSession,
            new Dictionary<string, object?> { ["sessionId"] = sessionId }, cancellationToken);

    private void OnNotification(string method, JsonElement? parameters)
    {
        if (method != AcpMethods.ClientSessionUpdate || parameters is not { } value)
            return;
        if (!value.TryGetProperty("sessionId", out var sessionIdElement) || sessionIdElement.ValueKind != JsonValueKind.String)
            return;
        if (!value.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
            return;
        var kind = update.TryGetProperty("sessionUpdate", out var kindElement) ? kindElement.GetString() ?? "" : "";
        string? text = null;
        if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            text = textElement.GetString();
        }
        SessionUpdate?.Invoke(new AcpSessionUpdate(sessionIdElement.GetString()!, kind, text, value.Clone()));
    }
}
