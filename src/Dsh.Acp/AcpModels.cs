using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Acp;

public sealed record AcpImplementationInfo(string Name, string? Version = null, string? Title = null);

public sealed record AcpFileSystemCapabilities(bool ReadTextFile, bool WriteTextFile);

/** This client never serves file system or terminal requests, so both stay off and agents use their own tools. */
public sealed record AcpClientCapabilities(AcpFileSystemCapabilities Fs, bool Terminal)
{
    public static AcpClientCapabilities None { get; } = new(new AcpFileSystemCapabilities(false, false), false);
}

public sealed record AcpInitializeRequest(int ProtocolVersion, AcpClientCapabilities ClientCapabilities, AcpImplementationInfo? ClientInfo);

public sealed record AcpAuthMethod(string Id, string Name, string? Description = null);

/** Capabilities are kept raw apart from the session-restoring ones the host needs to pick load or resume. */
public sealed record AcpInitializeResponse(
    int ProtocolVersion,
    JsonElement? AgentCapabilities,
    AcpImplementationInfo? AgentInfo,
    IReadOnlyList<AcpAuthMethod>? AuthMethods)
{
    /** `agentCapabilities.loadSession`: session/load replays the history as session/update before answering. */
    public bool CanLoadSession => AgentCapabilities is { ValueKind: JsonValueKind.Object } capabilities
        && capabilities.TryGetProperty("loadSession", out var load) && load.ValueKind == JsonValueKind.True;

    /** `agentCapabilities.sessionCapabilities.resume`: session/resume restores context without replaying history. */
    public bool CanResumeSession => AgentCapabilities is { ValueKind: JsonValueKind.Object } capabilities
        && capabilities.TryGetProperty("sessionCapabilities", out var session) && session.ValueKind == JsonValueKind.Object
        && session.TryGetProperty("resume", out var resume) && resume.ValueKind == JsonValueKind.Object;
}

/** MCP servers handed to the agent; this client passes none, so the list stays empty but must be present on the wire. */
public sealed record AcpSessionRequest(string Cwd, IReadOnlyList<JsonElement> McpServers, string? SessionId = null);

/** Response of session/new, session/load and session/resume; modes and config options are kept raw. */
public sealed record AcpSessionResponse(string? SessionId, JsonElement? Modes, JsonElement? ConfigOptions);

public sealed record AcpTextContent(string Type, string Text)
{
    public const string TextType = "text";

    public static AcpTextContent Of(string text) => new(TextType, text);
}

public sealed record AcpPromptRequest(string SessionId, IReadOnlyList<AcpTextContent> Prompt);

public sealed record AcpPromptResponse(string StopReason);

public sealed record AcpSessionIdParams(string SessionId);

/** One permission choice; Kind is one of the AcpMethods.Permission* values. */
public sealed record AcpPermissionOption(string OptionId, string Name, string Kind);

/** The tool call the agent asks about; Raw keeps every field (content, locations, rawInput) for display. */
public sealed record AcpToolCallInfo(string ToolCallId, string? Title, string? Kind, string? Status, JsonElement Raw);

public sealed record AcpPermissionOutcome(string Outcome, string? OptionId = null)
{
    public static AcpPermissionOutcome Cancelled { get; } = new(AcpMethods.OutcomeCancelled);

    public static AcpPermissionOutcome Selected(string optionId) => new(AcpMethods.OutcomeSelected, optionId);
}

public sealed record AcpPermissionResponse(AcpPermissionOutcome Outcome);

/** Request shape used only for decoding; AcpPermissionRequest is what callers see. */
internal sealed record AcpPermissionParams(string SessionId, JsonElement ToolCall, IReadOnlyList<AcpPermissionOption> Options);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AcpInitializeRequest))]
[JsonSerializable(typeof(AcpInitializeResponse))]
[JsonSerializable(typeof(AcpSessionRequest))]
[JsonSerializable(typeof(AcpSessionResponse))]
[JsonSerializable(typeof(AcpPromptRequest))]
[JsonSerializable(typeof(AcpPromptResponse))]
[JsonSerializable(typeof(AcpSessionIdParams))]
[JsonSerializable(typeof(AcpPermissionParams))]
[JsonSerializable(typeof(AcpPermissionResponse))]
internal sealed partial class AcpJsonContext : JsonSerializerContext;
