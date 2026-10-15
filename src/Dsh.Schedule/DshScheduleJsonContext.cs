using System.Text.Json.Serialization;

namespace Dsh.Schedule;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ScheduleChange))]
internal sealed partial class DshScheduleJsonContext : JsonSerializerContext
{
}
