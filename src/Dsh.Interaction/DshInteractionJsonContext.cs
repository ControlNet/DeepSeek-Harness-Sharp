using System.Text.Json.Serialization;

namespace Dsh.Interaction;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApprovalAskedPayload))]
[JsonSerializable(typeof(ApprovalDecidedPayload))]
[JsonSerializable(typeof(ApprovalPolicyPayload))]
[JsonSerializable(typeof(ApprovalGrantedPayload))]
[JsonSerializable(typeof(CommandRunPayload))]
[JsonSerializable(typeof(CommandDonePayload))]
[JsonSerializable(typeof(DeliverablesPresentedPayload))]
[JsonSerializable(typeof(PresentedFile))]
internal sealed partial class DshInteractionJsonContext : JsonSerializerContext
{
}

