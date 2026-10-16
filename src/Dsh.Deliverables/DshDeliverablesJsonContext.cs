using System.Text.Json.Serialization;
using Dsh.Interaction;

namespace Dsh.Deliverables;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PresentResult))]
[JsonSerializable(typeof(PresentedFile))]
internal sealed partial class DshDeliverablesJsonContext : JsonSerializerContext
{
}
