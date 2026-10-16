using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Core;

namespace Dsh.Schedule;

[JsonConverter(typeof(JsonStringEnumConverter<ScheduleKind>))]
public enum ScheduleKind
{
    [JsonStringEnumMemberName("after")]
    After,

    [JsonStringEnumMemberName("at")]
    At,

    [JsonStringEnumMemberName("every")]
    Every,

    [JsonStringEnumMemberName("daily")]
    Daily,

    [JsonStringEnumMemberName("weekly")]
    Weekly,

    [JsonStringEnumMemberName("cron")]
    Cron,
}

[JsonConverter(typeof(JsonStringEnumConverter<ScheduleOperation>))]
public enum ScheduleOperation
{
    [JsonStringEnumMemberName("create")]
    Create,

    [JsonStringEnumMemberName("update")]
    Update,

    [JsonStringEnumMemberName("delete")]
    Delete,

    [JsonStringEnumMemberName("dispatch")]
    Dispatch,
}

/** One reminder rule with its committed next UTC target; only the fields of its kind are set. */
public sealed record ScheduleRule(
    ScheduleKind Kind,
    DateTimeOffset ScheduledAt,
    long? AfterSeconds = null,
    long? EverySeconds = null,
    string? Time = null,
    string? TimeZone = null,
    IReadOnlyList<int>? Weekdays = null,
    string? Expression = null)
{
    public bool IsRecurring => Kind is not (ScheduleKind.After or ScheduleKind.At);
}

public sealed record ScheduleRecord(string Id, string Title, string Prompt, ScheduleRule Rule);

/** The occurrence a due reminder delivers, and the next target of a recurring one (null once it has ended). */
public readonly record struct ScheduleOccurrence(DateTimeOffset OccurrenceAt, DateTimeOffset? NextAt);

/** Result of an in-place edit: the stored record, or the miss code when the id is not active. */
public readonly record struct ScheduleUpdateResult(ScheduleRecord? Record, string? MissCode);

/** One versioned mutation of a session's reminders, persisted as a `schedule/change` session event. */
public sealed record ScheduleChange(
    ScheduleOperation Operation,
    ScheduleRecord? Schedule = null,
    string? Id = null,
    DateTimeOffset? OccurrenceAt = null,
    DateTimeOffset? NextAt = null)
{
    public const int ChangeVersion = 1;

    public int Version { get; init; } = ChangeVersion;
}

public sealed record ScheduleChangePayload(ScheduleChange Change) : SessionEventPayload
{
    public const string EventType = "schedule/change";

    public override string Type => EventType;

    internal static void RegisterCodec()
        => SessionEventCodec.Register<ScheduleChangePayload>(
            EventType,
            static (element, _) => new ScheduleChangePayload(Decode(element)),
            static (payload, writer, _) => JsonSerializer.Serialize(writer, payload.Change, DshScheduleJsonContext.Default.ScheduleChange),
            ignorable: true);

    private static ScheduleChange Decode(JsonElement element)
    {
        var change = element.Deserialize(DshScheduleJsonContext.Default.ScheduleChange)
            ?? throw new JsonException($"invalid \"{EventType}\" payload");
        if (change.Version != ScheduleChange.ChangeVersion)
            throw new JsonException($"unsupported \"{EventType}\" version {change.Version}");
        return change;
    }
}

public static class ScheduleErrorCodes
{
    public const string InvalidPrompt = "invalid_prompt";
    public const string InvalidSelector = "invalid_selector";
    public const string InvalidRule = "invalid_rule";
    public const string InvalidTimeZone = "invalid_time_zone";
    public const string NotFuture = "not_future";
    public const string TimeOutOfRange = "time_out_of_range";
    public const string FrequencyTooHigh = "frequency_too_high";
    public const string SubagentSession = "subagent_session";
    public const string InternalError = "internal_error";
    public const string NotFound = "schedule_not_found";
    public const string Ended = "schedule_ended";
}
