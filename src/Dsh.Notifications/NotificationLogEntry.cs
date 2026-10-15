using System.Text.Json.Serialization;

namespace Dsh.Notifications;

/** One JSONL line of the history file: either a new notification or a batch of read marks. */
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NotificationAdded), "added")]
[JsonDerivedType(typeof(NotificationsRead), "read")]
public abstract record NotificationLogEntry;

public sealed record NotificationAdded(NotificationRecord Notification) : NotificationLogEntry;

public sealed record NotificationsRead(IReadOnlyList<string> Ids) : NotificationLogEntry;

/** Source-generated metadata (trimming and NativeAOT safe); also lets remote protocols carry notification records. */
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(NotificationLogEntry))]
[JsonSerializable(typeof(NotificationRecord))]
[JsonSerializable(typeof(IReadOnlyList<NotificationRecord>))]
[JsonSerializable(typeof(NotificationFilter))]
public sealed partial class NotificationsJsonContext : JsonSerializerContext;
