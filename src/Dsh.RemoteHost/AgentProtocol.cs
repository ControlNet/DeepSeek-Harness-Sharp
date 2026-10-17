using System.Text.Json.Serialization;

namespace Dsh.RemoteHost;

/** Optional Agent wire contract; method and capability values are stable. */
public static class AgentProtocol
{
    public const string Capability = "agent.sessions";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodAgentCatalog, MethodAgentStart, MethodAgentPrompt, MethodAgentCancel, MethodAgentStop, MethodAgentResume]);

    public const string MethodAgentCatalog = "host.agent.catalog";
    public const string MethodAgentStart = "host.agent.start";
    public const string MethodAgentPrompt = "host.agent.prompt";
    public const string MethodAgentCancel = "host.agent.cancel";
    public const string MethodAgentStop = "host.agent.stop";
    public const string MethodAgentResume = "host.agent.resume";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteAgentCatalogEntry))]
[JsonSerializable(typeof(IReadOnlyList<RemoteAgentCatalogEntry>))]
[JsonSerializable(typeof(RemoteAgentStartRequest))]
[JsonSerializable(typeof(RemoteAgentPrompt))]
[JsonSerializable(typeof(RemoteHostSession))]
[JsonSerializable(typeof(RemoteSessionRef))]
[JsonSerializable(typeof(bool))]
public partial class AgentProtocolJsonContext : JsonSerializerContext;
