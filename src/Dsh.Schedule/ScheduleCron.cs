using System.Globalization;
using System.Numerics;
using System.Text;
using Dsh.Llm;
using NodaTime;

namespace Dsh.Schedule;

/** The strict upstream five-field dialect, including Vixie's first-character day-field star flags. */
internal sealed class ScheduleCron
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private static readonly FieldSpec Minute = new("minute", 0, 59, 59);
    private static readonly FieldSpec Hour = new("hour", 0, 23, 23);
    private static readonly FieldSpec DayOfMonth = new("day-of-month", 1, 31, 31);
    private static readonly FieldSpec Month = new("month", 1, 12, 12);
    private static readonly FieldSpec DayOfWeek = new("day-of-week", 0, 7, 6);

    private readonly ulong _daysOfMonth;
    private readonly ulong _months;
    private readonly ulong _daysOfWeek;
    private readonly bool _eitherDay;

    private ScheduleCron(Field minute, Field hour, Field dayOfMonth, Field month, Field dayOfWeek)
    {
        Expression = $"{minute.Canonical} {hour.Canonical} {dayOfMonth.Canonical} {month.Canonical} {dayOfWeek.Canonical}";
        Minutes = Values(minute.Mask);
        Hours = Values(hour.Mask);
        _daysOfMonth = dayOfMonth.Mask;
        _months = month.Mask;
        _daysOfWeek = dayOfWeek.Mask;
        _eitherDay = !dayOfMonth.Star && !dayOfWeek.Star;
    }

    public string Expression { get; }
    public int[] Hours { get; }
    public int[] Minutes { get; }

    public bool Matches(LocalDate date)
    {
        if ((_months & (1UL << date.Month)) == 0) return false;
        var dayOfMonth = (_daysOfMonth & (1UL << date.Day)) != 0;
        var dayOfWeek = (_daysOfWeek & (1UL << ((int)date.DayOfWeek % 7))) != 0;
        return _eitherDay ? dayOfMonth || dayOfWeek : dayOfMonth && dayOfWeek;
    }

    public static ScheduleCron Parse(string expression)
    {
        if (string.IsNullOrEmpty(expression) || IsWhitespace(expression[0]) || IsWhitespace(expression[^1]))
            throw Invalid("cron.expression must be a non-empty trimmed string.");

        Span<Range> fields = stackalloc Range[5];
        var count = 0;
        var start = 0;
        for (var index = 0; index <= expression.Length; index++)
        {
            if (index < expression.Length && !IsWhitespace(expression[index])) continue;
            if (count == fields.Length) throw FieldCount();
            fields[count++] = start..index;
            while (index + 1 < expression.Length && IsWhitespace(expression[index + 1])) index++;
            start = index + 1;
        }
        if (count != fields.Length) throw FieldCount();
        var text = expression.AsSpan();
        return new ScheduleCron(
            ParseField(text[fields[0]], Minute), ParseField(text[fields[1]], Hour),
            ParseField(text[fields[2]], DayOfMonth), ParseField(text[fields[3]], Month),
            ParseField(text[fields[4]], DayOfWeek));
    }

    private static Field ParseField(ReadOnlySpan<char> raw, FieldSpec spec)
    {
        ulong mask = 0;
        var start = 0;
        for (var index = 0; index <= raw.Length; index++)
        {
            if (index < raw.Length && raw[index] != ',') continue;
            var element = raw[start..index];
            if (element.IsEmpty)
                throw Invalid($"cron.expression {spec.Name} field must not contain an empty list element.");
            ParseElement(element, spec, ref mask);
            start = index + 1;
        }
        if (spec.CanonicalMax != spec.Max && (mask & (1UL << spec.Max)) != 0)
            mask = (mask & ~(1UL << spec.Max)) | (1UL << spec.Min);

        var star = raw[0] == '*';
        Span<int> values = stackalloc int[60];
        var length = FillValues(mask, values);
        return new Field(mask, star ? EncodeStar(mask, values[..length], spec) : Encode(values[..length]), star);
    }

    private static void ParseElement(ReadOnlySpan<char> element, FieldSpec spec, ref ulong mask)
    {
        if (element.SequenceEqual("*"))
        {
            AddWalk(ref mask, spec.Min, spec.Max, 1);
            return;
        }
        if (element[0] == '*')
        {
            if (element.Length < 3 || element[1] != '/' || !Digits(element[2..])) throw InvalidElement(spec, element);
            AddWalk(ref mask, spec.Min, spec.Max, Step(element[2..], spec));
            return;
        }

        var dash = element.IndexOf('-');
        var slash = element.IndexOf('/');
        var firstEnd = dash >= 0 ? dash : slash >= 0 ? slash : element.Length;
        var firstText = element[..firstEnd];
        if (!Digits(firstText)) throw InvalidElement(spec, element);
        if (dash < 0)
        {
            // Upstream does not accept n/step, even though several cron libraries do.
            if (slash >= 0) throw InvalidElement(spec, element);
            mask |= 1UL << Value(firstText, spec);
            return;
        }
        var lastEnd = slash >= 0 ? slash : element.Length;
        if (lastEnd <= dash || !Digits(element[(dash + 1)..lastEnd])
            || (slash >= 0 && !Digits(element[(slash + 1)..]))) throw InvalidElement(spec, element);
        var first = Value(firstText, spec);
        var last = Value(element[(dash + 1)..lastEnd], spec);
        if (first > last) throw Invalid($"cron.expression {spec.Name} field range {first}-{last} is inverted.");
        AddWalk(ref mask, first, last, slash >= 0 ? Step(element[(slash + 1)..], spec) : 1);
    }

    private static int Value(ReadOnlySpan<char> text, FieldSpec spec)
    {
        if (!TryNumber(text, spec.Max, out var value) || value < spec.Min)
            throw Invalid($"cron.expression {spec.Name} field value {text.ToString()} is outside {spec.Min}-{spec.Max}.");
        return (int)value;
    }

    private static long Step(ReadOnlySpan<char> text, FieldSpec spec)
        => TryNumber(text, MaxSafeInteger, out var step) && step > 0
            ? step
            : throw Invalid($"cron.expression {spec.Name} field step must be a positive integer.");

    private static bool TryNumber(ReadOnlySpan<char> text, long max, out long value)
    {
        value = 0;
        foreach (var character in text)
        {
            var digit = character - '0';
            if (value > (max - digit) / 10) return false;
            value = value * 10 + digit;
        }
        return value <= max;
    }

    private static bool Digits(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        foreach (var character in text)
            if (character is < '0' or > '9') return false;
        return true;
    }

    private static void AddWalk(ref ulong mask, int first, int last, long step)
    {
        for (long value = first; value <= last; value += step) mask |= 1UL << (int)value;
    }

    private static int[] Values(ulong mask)
    {
        var values = new int[BitOperations.PopCount(mask)];
        FillValues(mask, values);
        return values;
    }

    private static int FillValues(ulong mask, Span<int> values)
    {
        var length = 0;
        while (mask != 0)
        {
            values[length++] = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
        }
        return length;
    }

    private static string Encode(ReadOnlySpan<int> values)
    {
        if (values.Length == 1) return values[0].ToString(CultureInfo.InvariantCulture);
        var step = values[1] - values[0];
        var uniform = true;
        for (var index = 2; index < values.Length; index++)
            if (values[index] - values[index - 1] != step) { uniform = false; break; }
        if (uniform)
            return step == 1
                ? string.Create(CultureInfo.InvariantCulture, $"{values[0]}-{values[^1]}")
                : string.Create(CultureInfo.InvariantCulture, $"{values[0]}-{values[^1]}/{step}");

        var encoded = new StringBuilder();
        var runStart = values[0];
        for (var index = 1; index <= values.Length; index++)
        {
            if (index < values.Length && values[index] == values[index - 1] + 1) continue;
            if (encoded.Length > 0) encoded.Append(',');
            encoded.Append(CultureInfo.InvariantCulture, $"{runStart}");
            var previous = values[index - 1];
            if (runStart != previous) encoded.Append(CultureInfo.InvariantCulture, $"-{previous}");
            if (index < values.Length) runStart = values[index];
        }
        return encoded.ToString();
    }

    private static string EncodeStar(ulong mask, ReadOnlySpan<int> values, FieldSpec spec)
    {
        if (values.Length == spec.CanonicalMax - spec.Min + 1) return "*";
        ulong bestWalk = 0;
        var bestStep = 0;
        var bestLength = 0;
        for (var step = 1; step <= spec.Max - spec.Min + 1; step++)
        {
            ulong walk = 0;
            AddWalk(ref walk, spec.Min, spec.Max, step);
            if (spec.CanonicalMax != spec.Max && (walk & (1UL << spec.Max)) != 0)
                walk = (walk & ~(1UL << spec.Max)) | (1UL << spec.Min);
            var length = BitOperations.PopCount(walk);
            if (length <= bestLength || (walk & mask) != walk) continue;
            bestStep = step;
            bestWalk = walk;
            bestLength = length;
        }
        var walkText = string.Create(CultureInfo.InvariantCulture, $"*/{bestStep}");
        if (mask == bestWalk) return walkText;
        Span<int> remaining = stackalloc int[60];
        var count = FillValues(mask & ~bestWalk, remaining);
        return $"{walkText},{Encode(remaining[..count])}";
    }

    // ECMAScript \s/trim, deliberately not .NET's differing Unicode whitespace set.
    private static bool IsWhitespace(char value)
        => value is >= '\u0009' and <= '\u000D' or '\u0020' or '\u00A0' or '\u1680'
            or >= '\u2000' and <= '\u200A' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    private static HarnessException FieldCount()
        => Invalid("cron.expression must contain exactly five whitespace-separated fields: minute hour day-of-month month day-of-week.");

    private static HarnessException InvalidElement(FieldSpec spec, ReadOnlySpan<char> element)
        => Invalid($"cron.expression {spec.Name} field element \"{element.ToString()}\" must be *, a value, a-b, */n, a-b/n, or a comma-separated list of those.");

    private static HarnessException Invalid(string message) => new(message, ScheduleErrorCodes.InvalidRule);
    private readonly record struct FieldSpec(string Name, int Min, int Max, int CanonicalMax);
    private readonly record struct Field(ulong Mask, string Canonical, bool Star);
}
