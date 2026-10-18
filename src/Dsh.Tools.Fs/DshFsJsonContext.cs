using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Core;

namespace Dsh.Tools;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReadResultValue))]
[JsonSerializable(typeof(ReadFileLine))]
[JsonSerializable(typeof(ReadImageValue))]
[JsonSerializable(typeof(WriteResultValue))]
[JsonSerializable(typeof(EditResultValue))]
[JsonSerializable(typeof(WorkingDirectoryValue))]
[JsonSerializable(typeof(CreatedWorktreeValue))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class DshFsJsonContext : JsonSerializerContext
{
}
