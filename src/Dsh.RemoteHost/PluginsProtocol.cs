using System.Text.Json.Serialization;

namespace Dsh.RemoteHost;

/** Optional Plugins wire contract; method and capability values are stable. */
public static class PluginsProtocol
{
    public const string Capability = "plugins.manage";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodPluginsList, MethodPluginPermission]);

    public const string MethodPluginsList = "host.plugins.list";
    public const string MethodPluginPermission = "host.plugins.permission";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemotePluginInfo))]
[JsonSerializable(typeof(IReadOnlyList<RemotePluginInfo>))]
[JsonSerializable(typeof(RemotePluginPermissionRequest))]
[JsonSerializable(typeof(string))]
public partial class PluginsProtocolJsonContext : JsonSerializerContext;
