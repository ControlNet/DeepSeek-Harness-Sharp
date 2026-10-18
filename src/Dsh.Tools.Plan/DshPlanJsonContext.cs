using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Core;

namespace Dsh.Tools;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TodoWritePayload))]
[JsonSerializable(typeof(TodoItem))]
[JsonSerializable(typeof(TodoWriteResult))]
[JsonSerializable(typeof(TodoCounts))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class DshPlanJsonContext : JsonSerializerContext
{
}
