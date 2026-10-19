using System.Text.Json.Serialization;

namespace Dsh.RemoteHost;

/** Optional target-host outbound network configuration contract. */
public static class NetworkProtocol
{
    public const string Capability = "network.outbound";
    public const int RevisionConflict = -32009;
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodOutboundGet, MethodOutboundSet, MethodOutboundProbe]);

    public const string MethodOutboundGet = "host.network.outbound.get";
    public const string MethodOutboundSet = "host.network.outbound.set";
    public const string MethodOutboundProbe = "host.network.outbound.probe";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RemoteOutboundProxySettings))]
[JsonSerializable(typeof(RemoteOutboundProxyState))]
[JsonSerializable(typeof(RemoteOutboundProxySetRequest))]
[JsonSerializable(typeof(RemoteOutboundProxyProbeRequest))]
[JsonSerializable(typeof(RemoteOutboundProxyProbeResult))]
public partial class NetworkProtocolJsonContext : JsonSerializerContext;
