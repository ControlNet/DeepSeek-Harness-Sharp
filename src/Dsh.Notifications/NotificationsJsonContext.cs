using System.Text.Json.Serialization;

namespace Dsh.Notifications;

/** Source-generated metadata (trimming and NativeAOT safe); also lets remote protocols carry notification records. */
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(NotificationRecord))]
[JsonSerializable(typeof(IReadOnlyList<NotificationRecord>))]
[JsonSerializable(typeof(NotificationFilter))]
public sealed partial class NotificationsJsonContext : JsonSerializerContext;
