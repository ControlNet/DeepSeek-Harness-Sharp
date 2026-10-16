using System.Globalization;
using System.Text.RegularExpressions;
using Cronos;
using Dsh.Llm;

namespace Dsh.Schedule;

/** One timing selector as the model supplied it, before validation. */
public abstract record ScheduleTiming
{
    private ScheduleTiming()
    {
    }

    public sealed record After(long Seconds) : ScheduleTiming;

    public sealed record AtInstant(string Text) : ScheduleTiming;

    public sealed record AtLocal(string Date, string Time, string TimeZone) : ScheduleTiming;

    public sealed record Every(long Seconds) : ScheduleTiming;

    public sealed record Daily(string Time, string TimeZone) : ScheduleTiming;

    public sealed record Weekly(string Time, string TimeZone, IReadOnlyList<int> Weekdays) : ScheduleTiming;

    public sealed record Cron(string Expression, string TimeZone) : ScheduleTiming;
}

/**
 * Turns timing selectors into rules with a committed UTC target, and advances due rules.
 * Daily, weekly and cron rules are evaluated by Cronos in their explicit time zone:
 * a local time skipped by a daylight-saving jump fires at the end of the jump, and a repeated local time fires once.
 */
public static partial class ScheduleRules
{
    public const long MinEverySeconds = 60;

    private const int IsoMonday = 1;
    private const int IsoSunday = 7;
    private const string DateFormat = "yyyy-MM-dd";
    private const string StoredTimeFormat = "HH:mm:ss";
    private static readonly string[] TimeFormats = ["HH:mm:ss", "HH:mm"];

    [GeneratedRegex("""^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d{1,9})?([Zz]|[+-]\d{2}:\d{2})$""")]
    private static partial Regex Rfc3339WithOffset();

    public static ScheduleRule Resolve(ScheduleTiming timing, DateTimeOffset now) => timing switch
    {
        ScheduleTiming.After { Seconds: <= 0 } => throw new HarnessException("after_seconds must be a positive integer", ScheduleErrorCodes.InvalidRule),
        ScheduleTiming.After after => new ScheduleRule(ScheduleKind.After, Later(now, after.Seconds), AfterSeconds: after.Seconds),
        ScheduleTiming.AtInstant at => Absolute(ParseInstant(at.Text), now),
        ScheduleTiming.AtLocal at => Absolute(ResolveLocal(at), now),
        ScheduleTiming.Every { Seconds: < MinEverySeconds } => throw new HarnessException(
            $"every_seconds must be at least {MinEverySeconds}", ScheduleErrorCodes.FrequencyTooHigh),
        ScheduleTiming.Every every => new ScheduleRule(ScheduleKind.Every, Later(now, every.Seconds), EverySeconds: every.Seconds),
        ScheduleTiming.Daily daily => WallClock(new ScheduleRule(ScheduleKind.Daily, now, Time: NormalizeTime(daily.Time), TimeZone: daily.TimeZone), now),
        ScheduleTiming.Weekly weekly => WallClock(new ScheduleRule(
            ScheduleKind.Weekly, now, Time: NormalizeTime(weekly.Time), TimeZone: weekly.TimeZone, Weekdays: NormalizeWeekdays(weekly.Weekdays)), now),
        ScheduleTiming.Cron cron => WallClock(new ScheduleRule(ScheduleKind.Cron, now, TimeZone: cron.TimeZone, Expression: cron.Expression.Trim()), now),
        _ => throw new ArgumentOutOfRangeException(nameof(timing)),
    };

    /** The occurrence a due rule delivers: one-shots end; recurring rules deliver only their latest missed occurrence. */
    public static ScheduleOccurrence Occur(ScheduleRule rule, DateTimeOffset now) => rule.Kind switch
    {
        ScheduleKind.After or ScheduleKind.At => new ScheduleOccurrence(rule.ScheduledAt, null),
        ScheduleKind.Every => EveryOccurrence(rule, now),
        _ => WallClockOccurrence(rule, now),
    };

    public static TimeZoneInfo Zone(string id)
    {
        try
        {
            // Windows invariant-globalization releases cannot use the runtime's ICU-based IANA mapping.
            if (OperatingSystem.IsWindows() && TimeZoneConverter.TZConvert.TryIanaToWindows(id, out var windowsId))
                id = windowsId;
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new HarnessException(
                $"unknown time zone \"{id}\"; use UTC, an IANA Area/Location name, or the host zone \"{TimeZoneInfo.Local.Id}\"",
                ScheduleErrorCodes.InvalidTimeZone);
        }
    }

    private static ScheduleOccurrence EveryOccurrence(ScheduleRule rule, DateTimeOffset now)
    {
        var interval = TimeSpan.FromSeconds(rule.EverySeconds!.Value);
        var missed = (now - rule.ScheduledAt).Ticks / interval.Ticks;
        var occurrence = rule.ScheduledAt + TimeSpan.FromTicks(missed * interval.Ticks);
        DateTimeOffset? next = DateTimeOffset.MaxValue - occurrence > interval ? occurrence + interval : null;
        return new ScheduleOccurrence(occurrence, next);
    }

    private static ScheduleOccurrence WallClockOccurrence(ScheduleRule rule, DateTimeOffset now)
    {
        var cron = CronOf(rule);
        var zone = Zone(rule.TimeZone!);
        var latest = cron.GetPreviousOccurrence(now, zone, inclusive: true);
        var occurrence = latest > rule.ScheduledAt ? latest.Value.ToUniversalTime() : rule.ScheduledAt;
        return new ScheduleOccurrence(occurrence, cron.GetNextOccurrence(now, zone)?.ToUniversalTime());
    }

    private static ScheduleRule WallClock(ScheduleRule rule, DateTimeOffset now)
    {
        var next = CronOf(rule).GetNextOccurrence(now, Zone(rule.TimeZone!))
            ?? throw new HarnessException("the rule has no future occurrence", ScheduleErrorCodes.TimeOutOfRange);
        return rule with { ScheduledAt = next.ToUniversalTime() };
    }

    private static CronExpression CronOf(ScheduleRule rule)
    {
        if (rule.Kind == ScheduleKind.Cron)
        {
            return CronExpression.TryParse(rule.Expression!, CronFormat.Standard, out var parsed)
                ? parsed
                : throw new HarnessException(
                    $"invalid cron expression \"{rule.Expression}\"; expected five fields: minute hour day-of-month month day-of-week",
                    ScheduleErrorCodes.InvalidRule);
        }
        var time = ParseTime(rule.Time!);
        var weekdays = rule.Weekdays is { } days ? string.Join(',', days.Select(day => day % IsoSunday)) : "*";
        return CronExpression.Parse($"{time.Second} {time.Minute} {time.Hour} * * {weekdays}", CronFormat.IncludeSeconds);
    }

    private static ScheduleRule Absolute(DateTimeOffset target, DateTimeOffset now)
        => target > now
            ? new ScheduleRule(ScheduleKind.At, target.ToUniversalTime())
            : throw new HarnessException($"target {target:O} is not in the future", ScheduleErrorCodes.NotFuture);

    private static DateTimeOffset ParseInstant(string text)
    {
        if (Rfc3339WithOffset().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            return instant;
        throw new HarnessException(
            $"at \"{text}\" must be an RFC 3339 date-time with an offset, for example 2026-01-02T09:00:00+08:00",
            ScheduleErrorCodes.InvalidRule);
    }

    private static DateTimeOffset ResolveLocal(ScheduleTiming.AtLocal at)
    {
        if (!DateOnly.TryParseExact(at.Date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new HarnessException($"at.date \"{at.Date}\" must be {DateFormat}", ScheduleErrorCodes.InvalidRule);
        var zone = Zone(at.TimeZone);
        var local = date.ToDateTime(ParseTime(at.Time));
        if (zone.IsInvalidTime(local))
            throw new HarnessException($"{at.Date} {at.Time} does not exist in {at.TimeZone}", ScheduleErrorCodes.InvalidRule);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static TimeOnly ParseTime(string text)
        => TimeOnly.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new HarnessException($"time \"{text}\" must be {StoredTimeFormat}", ScheduleErrorCodes.InvalidRule);

    private static string NormalizeTime(string text) => ParseTime(text).ToString(StoredTimeFormat, CultureInfo.InvariantCulture);

    private static IReadOnlyList<int> NormalizeWeekdays(IReadOnlyList<int> weekdays)
    {
        if (weekdays.Count == 0 || weekdays.Any(day => day is < IsoMonday or > IsoSunday) || weekdays.Distinct().Count() != weekdays.Count)
        {
            throw new HarnessException(
                "weekly.weekdays must list ISO weekdays, Monday 1 through Sunday 7, without repetitions",
                ScheduleErrorCodes.InvalidRule);
        }
        return [.. weekdays.Order()];
    }

    private static DateTimeOffset Later(DateTimeOffset now, long seconds)
        => seconds < (DateTimeOffset.MaxValue - now).TotalSeconds
            ? now.AddSeconds(seconds)
            : throw new HarnessException("the target time is out of range", ScheduleErrorCodes.TimeOutOfRange);
}
