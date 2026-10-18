using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Core;

namespace Dsh.Tools;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GlobResultValue))]
[JsonSerializable(typeof(GrepMatchValue))]
[JsonSerializable(typeof(GrepResultValue))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class DshSearchJsonContext : JsonSerializerContext
{
}
