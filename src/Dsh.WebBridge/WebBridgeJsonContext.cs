using System.Text.Json.Serialization;
using Dsh.Fleet;

namespace Dsh.WebBridge;

/** Source-generated JSON of the bridge's own HTTP API; property names stay PascalCase like the host protocol, enums are strings. */
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(IReadOnlyList<FleetMachineStatus>))]
internal sealed partial class WebBridgeJsonContext : JsonSerializerContext;
