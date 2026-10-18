using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Core;

namespace Dsh.Tools;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BashStreamOutput))]
[JsonSerializable(typeof(BashRunValue))]
[JsonSerializable(typeof(SubprocessOutputRead))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class DshShellJsonContext : JsonSerializerContext
{
}
