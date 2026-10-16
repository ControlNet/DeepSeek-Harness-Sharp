using System.Globalization;
using System.Text.RegularExpressions;
using NodaTime;
using NodaTime.TimeZones;
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
 * The upstream numeric-cron and local-calendar contract. Calendar matching uses
 * Gregorian dates; timezone transitions are resolved at the actual UTC instant.
 */
public static partial class ScheduleRules
{
    public const long MinEverySeconds = 60;
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private const long DayMilliseconds = 86_400_000;
    private const long GregorianCycleDays = 146_097;
    private static readonly LocalDate CalendarAnchor = new(2000, 1, 1);
    private static readonly long AnchorDay = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() / DayMilliseconds;
    private static readonly long Minimum = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long Maximum = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
    private static readonly long ZoneMinimum = Instant.MinValue.ToUnixTimeMilliseconds();
    private static readonly Dictionary<string, string> ZoneIds = LoadZoneIds();

    [GeneratedRegex(@"^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})T(?<time>[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,3})?)(?:Z|(?<sign>[+-])(?<hour>[0-9]{2}):(?<minute>[0-9]{2}))\z")]
    private static partial Regex OffsetInstant();

    [GeneratedRegex(@"^(?<hour>[0-9]{2}):(?<minute>[0-9]{2}):(?<second>[0-9]{2})(?:\.(?<fraction>[0-9]{1,3}))?\z")]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial Regex LocalDateText();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+.-]*(?:/[A-Za-z0-9_+.-]+)+\z")]
    private static partial Regex IanaZone();

    public static string HostTimeZone
    {
        get
        {
            var id = TimeZoneInfo.Local.Id;
            if (ZoneIds.TryGetValue(id, out var canonical))
                return canonical;
            return TzdbDateTimeZoneSource.Default.WindowsToTzdbIds.TryGetValue(id, out var iana)
                && ZoneIds.TryGetValue(iana, out canonical) ? canonical : "UTC";
        }
    }

    public static ScheduleRule Resolve(ScheduleTiming timing, DateTimeOffset now)
    {
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        return timing switch
        {
            ScheduleTiming.After after => new ScheduleRule(ScheduleKind.After, Later(now, after.Seconds), AfterSeconds: after.Seconds),
            ScheduleTiming.AtInstant at => Absolute(ParseInstant(at.Text), now),
            ScheduleTiming.AtLocal at => Absolute(ParseLocal(at), now),
            ScheduleTiming.Every { Seconds: > MaxSafeInteger or < -MaxSafeInteger } => throw new HarnessException(
                "every_seconds must be a safe integer", ScheduleErrorCodes.InvalidRule),
            ScheduleTiming.Every { Seconds: < MinEverySeconds } => throw new HarnessException(
                $"every_seconds must be at least {MinEverySeconds}", ScheduleErrorCodes.FrequencyTooHigh),
            ScheduleTiming.Every every => new ScheduleRule(ScheduleKind.Every, Later(now, every.Seconds), EverySeconds: every.Seconds),
            ScheduleTiming.Daily daily => WallClock(new ScheduleRule(ScheduleKind.Daily, now, Time: daily.Time, TimeZone: daily.TimeZone), now),
            ScheduleTiming.Weekly weekly => WallClock(new ScheduleRule(
                ScheduleKind.Weekly, now, Time: weekly.Time, TimeZone: weekly.TimeZone, Weekdays: weekly.Weekdays), now),
            ScheduleTiming.Cron cron => WallClock(new ScheduleRule(ScheduleKind.Cron, now, TimeZone: cron.TimeZone, Expression: cron.Expression), now),
            _ => throw new ArgumentOutOfRangeException(nameof(timing)),
        };
    }

    /** Decisions select the latest due occurrence, not an accumulated backlog. */
    public static ScheduleOccurrence Occur(ScheduleRule rule, DateTimeOffset now)
    {
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        if (now < rule.ScheduledAt)
            throw new HarnessException("dispatch cannot precede the committed scheduledAt", ScheduleErrorCodes.CorruptLog);
        return rule.Kind switch
        {
            ScheduleKind.After or ScheduleKind.At => new ScheduleOccurrence(rule.ScheduledAt, null),
            ScheduleKind.Every => EveryOccurrence(rule, now),
            _ => WallClockOccurrence(rule, now),
        };
    }

    public static DateTimeZone Zone(string id)
    {
        if ((id == "UTC" || IanaZone().IsMatch(id)) && ZoneIds.TryGetValue(id, out var canonical))
            return CanonicalZone(canonical);
        throw new HarnessException("time_zone must be UTC or a valid IANA Area/Location name", ScheduleErrorCodes.InvalidTimeZone);
    }

    private static DateTimeZone CanonicalZone(string id)
        => id is "Etc/UTC" or "Etc/GMT" or "UTC" ? DateTimeZone.Utc : DateTimeZoneProviders.Tzdb[id];

    private static Dictionary<string, string> LoadZoneIds()
    {
        using var stream = typeof(ScheduleRules).Assembly.GetManifestResourceStream("Dsh.Schedule.ScheduleTimeZones.tsv")
            ?? throw new InvalidOperationException("The bundled timezone aliases are missing.");
        using var reader = new StreamReader(stream);
        var zones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var separator = line.IndexOf('\t');
            zones.Add(line[..separator], line[(separator + 1)..]);
        }
        return zones;
    }

    private static ScheduleRule WallClock(ScheduleRule rule, DateTimeOffset now)
    {
        var cron = rule.Kind == ScheduleKind.Cron ? ScheduleCron.Parse(rule.Expression!) : null;
        var time = cron is null ? ParseTime(rule.Time!) : default;
        var zone = Zone(rule.TimeZone!);
        rule = rule with
        {
            Time = cron is null ? FormatTime(time) : null,
            TimeZone = ZoneIds[rule.TimeZone!],
            Weekdays = rule.Kind == ScheduleKind.Weekly ? NormalizeWeekdays(rule.Weekdays!) : null,
            Expression = cron?.Expression,
        };
        var next = FindTarget(rule, cron, time, zone, now.ToUnixTimeMilliseconds(), forward: true)
            ?? throw new HarnessException("the rule has no future four-digit-year occurrence", ScheduleErrorCodes.TimeOutOfRange);
        return rule with { ScheduledAt = DateTimeOffset.FromUnixTimeMilliseconds(next) };
    }

    private static ScheduleOccurrence EveryOccurrence(ScheduleRule rule, DateTimeOffset now)
    {
        var interval = TimeSpan.FromSeconds(rule.EverySeconds!.Value);
        var missed = (now - rule.ScheduledAt).Ticks / interval.Ticks;
        var occurrence = rule.ScheduledAt + TimeSpan.FromTicks(missed * interval.Ticks);
        DateTimeOffset? next = DateTimeOffset.FromUnixTimeMilliseconds(Maximum) - occurrence >= interval ? occurrence + interval : null;
        return new ScheduleOccurrence(occurrence, next);
    }

    private static ScheduleOccurrence WallClockOccurrence(ScheduleRule rule, DateTimeOffset now)
    {
        var cron = rule.Kind == ScheduleKind.Cron ? ScheduleCron.Parse(rule.Expression!) : null;
        var time = cron is null ? ParseTime(rule.Time!) : default;
        var zone = Zone(rule.TimeZone!);
        var decision = now.ToUnixTimeMilliseconds();
        var latest = FindTarget(rule, cron, time, zone, decision, forward: false);
        var occurrence = Math.Max(latest ?? Minimum, rule.ScheduledAt.ToUnixTimeMilliseconds());
        // Daily/weekly rules consume a local date; a pinned target must not repeat that date.
        long? afterDay = cron is null ? LocalDay(occurrence, zone) : null;
        var next = FindTarget(rule, cron, time, zone, decision, forward: true, afterDay);
        return new ScheduleOccurrence(DateTimeOffset.FromUnixTimeMilliseconds(occurrence),
            next is { } target ? DateTimeOffset.FromUnixTimeMilliseconds(target) : null);
    }

    private static long? FindTarget(
        ScheduleRule rule, ScheduleCron? cron, LocalTime clock, DateTimeZone zone, long decision, bool forward, long? afterDay = null)
    {
        var localDecision = decision + OffsetMilliseconds(zone.GetUtcOffset(Instant.FromUnixTimeMilliseconds(decision)));
        var decisionDay = FloorDay(localDecision);
        var firstDay = forward ? decisionDay : FloorDay(decision) + 1;
        if (afterDay is { } delivered && firstDay <= delivered)
            firstDay = delivered + 1;
        var lastDay = forward
            ? Math.Min(firstDay + GregorianCycleDays, FloorDay(Maximum) + 1)
            : Math.Max(firstDay - GregorianCycleDays, LocalDay(Minimum, zone));
        var times = cron is null ? 1 : cron.Hours.Length * cron.Minutes.Length;
        var floorClock = localDecision - decisionDay * DayMilliseconds;
        for (var day = firstDay; forward ? day <= lastDay : day >= lastDay; day += forward ? 1 : -1)
        {
            var date = CalendarDate(day);
            if (cron is not null ? !cron.Matches(date)
                : rule.Kind == ScheduleKind.Weekly && !rule.Weekdays!.Contains((int)date.DayOfWeek))
                continue;
            if (!forward)
            {
                long? earliest = null;
                for (var index = 0; index < times && earliest is null; index++)
                    earliest = LocalInstant(day, TimeAt(cron, clock, index), zone);
                if (earliest > decision)
                    continue;
            }
            for (var index = forward ? 0 : times - 1; forward ? index < times : index >= 0; index += forward ? 1 : -1)
            {
                var time = TimeAt(cron, clock, index);
                if (forward && day == decisionDay && time.TickOfDay / TimeSpan.TicksPerMillisecond < floorClock)
                    continue;
                var candidate = LocalInstant(day, time, zone);
                if (candidate is not { } instant || instant < Minimum || instant > Maximum)
                    continue;
                if (forward ? instant > decision : instant <= decision)
                    return instant;
            }
        }
        return null;
    }

    private static LocalTime TimeAt(ScheduleCron? cron, LocalTime clock, int index)
        => cron is null ? clock : new LocalTime(cron.Hours[index / cron.Minutes.Length], cron.Minutes[index % cron.Minutes.Length]);

    // Gregorian month/day/weekday repeat every 400 years. Matching in an equivalent
    // cycle also supports local year 0/10000 at the four-digit UTC boundary. Only the
    // calendar is shifted: timezone offsets always come from the actual instant.
    private static LocalDate CalendarDate(long localDay)
        => CalendarAnchor.PlusDays((int)(((localDay - AnchorDay) % GregorianCycleDays + GregorianCycleDays) % GregorianCycleDays));

    private static long FloorDay(long milliseconds)
        => Math.DivRem(milliseconds, DayMilliseconds, out var remainder) - (remainder < 0 ? 1 : 0);

    private static long LocalDay(long instant, DateTimeZone zone)
        => FloorDay(instant + OffsetMilliseconds(zone.GetUtcOffset(Instant.FromUnixTimeMilliseconds(instant))));

    private static long OffsetMilliseconds(Offset offset) => offset.ToTimeSpan().Ticks / TimeSpan.TicksPerMillisecond;

    /** Map a wall clock by actual zone intervals: a gap has no candidate; the first interval wins an overlap. */
    private static long? LocalInstant(long day, LocalTime time, DateTimeZone zone)
    {
        var local = day * DayMilliseconds + time.TickOfDay / TimeSpan.TicksPerMillisecond;
        var cursor = Instant.FromUnixTimeMilliseconds(Math.Clamp(local - DayMilliseconds, ZoneMinimum, Maximum));
        var ceiling = Instant.FromUnixTimeMilliseconds(Math.Clamp(local + DayMilliseconds, ZoneMinimum, Maximum));
        while (true)
        {
            var interval = zone.GetZoneInterval(cursor);
            var candidate = local - OffsetMilliseconds(interval.WallOffset);
            if ((!interval.HasStart || candidate >= interval.Start.ToUnixTimeMilliseconds())
                && (!interval.HasEnd || candidate < interval.End.ToUnixTimeMilliseconds()))
                return candidate;
            if (!interval.HasEnd || interval.End > ceiling)
                return null;
            cursor = interval.End;
        }
    }

    private static ScheduleRule Absolute(long target, DateTimeOffset now)
    {
        if (target < Minimum || target > Maximum)
            throw new HarnessException("the target must be a four-digit-year UTC instant", ScheduleErrorCodes.TimeOutOfRange);
        return target > now.ToUnixTimeMilliseconds()
            ? new ScheduleRule(ScheduleKind.At, DateTimeOffset.FromUnixTimeMilliseconds(target))
            : throw new HarnessException("the target must be strictly in the future", ScheduleErrorCodes.NotFuture);
    }

    private static long ParseInstant(string text)
    {
        var match = OffsetInstant().Match(text);
        if (!match.Success)
            throw new HarnessException("at requires YYYY-MM-DDTHH:mm:ss, optional 1-3 fractional digits and Z or a numeric offset", ScheduleErrorCodes.InvalidRule);
        var date = ParseDate(match.Groups["date"].Value);
        var time = ParseTime(match.Groups["time"].Value);
        var local = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds()
            + time.TickOfDay / TimeSpan.TicksPerMillisecond;
        if (!match.Groups["sign"].Success)
            return local;
        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
        var negative = match.Groups["sign"].Value == "-";
        if (hour > 23 || minute > 59 || negative && hour == 0 && minute == 0)
            throw new HarnessException("at has an invalid numeric offset", ScheduleErrorCodes.InvalidRule);
        return local - (negative ? -1 : 1) * (hour * 60L + minute) * 60_000;
    }

    private static long ParseLocal(ScheduleTiming.AtLocal at)
    {
        var date = ParseDate(at.Date);
        var time = ParseTime(at.Time);
        var zone = Zone(at.TimeZone);
        var day = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds() / DayMilliseconds;
        return LocalInstant(day, time, zone)
            ?? throw new HarnessException("the local at time does not exist in the selected time zone", ScheduleErrorCodes.InvalidRule);
    }

    private static DateOnly ParseDate(string text)
        => LocalDateText().IsMatch(text)
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date : throw new HarnessException("date must be a real YYYY-MM-DD calendar date", ScheduleErrorCodes.InvalidRule);

    private static LocalTime ParseTime(string text)
    {
        var match = ClockTime().Match(text);
        if (match.Success)
        {
            var hour = int.Parse(match.Groups["hour"].ValueSpan, CultureInfo.InvariantCulture);
            var minute = int.Parse(match.Groups["minute"].ValueSpan, CultureInfo.InvariantCulture);
            var second = int.Parse(match.Groups["second"].ValueSpan, CultureInfo.InvariantCulture);
            if (hour <= 23 && minute <= 59 && second <= 59)
            {
                var fraction = match.Groups["fraction"].ValueSpan;
                var millisecond = fraction.Length == 0 ? 0 : int.Parse(fraction, CultureInfo.InvariantCulture) * (fraction.Length == 1 ? 100 : fraction.Length == 2 ? 10 : 1);
                return new LocalTime(hour, minute, second, millisecond);
            }
        }
        throw new HarnessException("time requires HH:mm:ss with optional 1-3 fractional digits", ScheduleErrorCodes.InvalidRule);
    }

    private static string FormatTime(LocalTime time)
        => string.Create(CultureInfo.InvariantCulture, $"{time.Hour:00}:{time.Minute:00}:{time.Second:00}.{time.Millisecond:000}");

    private static IReadOnlyList<int> NormalizeWeekdays(IReadOnlyList<int> weekdays)
    {
        var mask = 0;
        foreach (var day in weekdays)
        {
            if (day is < 1 or > 7 || (mask & (1 << day)) != 0)
                throw new HarnessException("weekly.weekdays must contain unique ISO weekdays 1 through 7", ScheduleErrorCodes.InvalidRule);
            mask |= 1 << day;
        }
        if (mask == 0)
            throw new HarnessException("weekly.weekdays must not be empty", ScheduleErrorCodes.InvalidRule);
        var normalized = new int[weekdays.Count];
        var index = 0;
        for (var day = 1; day <= 7; day++)
            if ((mask & (1 << day)) != 0)
                normalized[index++] = day;
        return normalized;
    }

    private static DateTimeOffset Later(DateTimeOffset now, long seconds)
    {
        if (seconds <= 0 || seconds > MaxSafeInteger)
            throw new HarnessException("seconds must be a positive safe integer", ScheduleErrorCodes.InvalidRule);
        if (seconds > (Maximum - now.ToUnixTimeMilliseconds()) / 1000)
            throw new HarnessException("the target time is out of range", ScheduleErrorCodes.TimeOutOfRange);
        return now.AddSeconds(seconds);
    }
}
