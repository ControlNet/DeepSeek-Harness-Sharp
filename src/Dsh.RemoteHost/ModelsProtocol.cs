using System.Text.Json.Serialization;

namespace Dsh.RemoteHost;

/** Optional Models wire contract; method and capability values are stable. */
public static class ModelsProtocol
{
    public const string Capability = "models.catalog";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodReasoningEfforts, MethodProviderModels]);

    public const string MethodReasoningEfforts = "host.models.reasoningEfforts";
    public const string MethodProviderModels = "host.models.list";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteModelRef))]
[JsonSerializable(typeof(RemoteProviderRef))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
public partial class ModelsProtocolJsonContext : JsonSerializerContext;
