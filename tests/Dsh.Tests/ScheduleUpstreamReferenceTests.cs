using System.Globalization;
using System.Text.Json;
using Dsh.Llm;
using Dsh.Schedule;

namespace Dsh.Tests;

/** Golden values were produced by executing the unmodified, pinned upstream domain module. */
public sealed class ScheduleUpstreamReferenceTests
{
    public static TheoryData<string> Cases()
    {
        using var stream = typeof(ScheduleUpstreamReferenceTests).Assembly.GetManifestResourceStream("Dsh.Tests.ScheduleUpstreamFixtures.json")!;
        using var document = JsonDocument.Parse(stream);
        Assert.Equal("86a402200d95fecef4529d969836f1fe13c6cb0d", document.RootElement.GetProperty("source").GetString());
        var cases = new TheoryData<string>();
        foreach (var fixture in document.RootElement.GetProperty("cases").EnumerateArray())
            cases.Add(fixture.GetRawText());
        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesExecutedUpstream(string json)
    {
        using var document = JsonDocument.Parse(json);
        var fixture = document.RootElement;
        var timing = Timing(fixture.GetProperty("kind").GetString()!, fixture.GetProperty("selector"));
        var now = Utc(fixture.GetProperty("now").GetString()!);
        if (fixture.TryGetProperty("code", out var code))
        {
            var error = Assert.Throws<HarnessException>(() =>
            {
                var rejected = ScheduleRules.Resolve(timing, now);
                if (fixture.TryGetProperty("decision", out var invalidDecision))
                    ScheduleRules.Occur(rejected, Utc(invalidDecision.GetString()!));
            });
            Assert.Equal(code.GetString(), error.Code);
            return;
        }

        var rule = ScheduleRules.Resolve(timing, now);
        var expected = fixture.GetProperty("expected");
        Assert.Equal(expected.GetProperty("scheduledAt").GetString(), ScheduleFraming.Instant(rule.ScheduledAt));
        if (expected.TryGetProperty("time", out var time)) Assert.Equal(time.GetString(), rule.Time);
        if (expected.TryGetProperty("timeZone", out var zone)) Assert.Equal(zone.GetString(), rule.TimeZone);
        if (expected.TryGetProperty("expression", out var expression)) Assert.Equal(expression.GetString(), rule.Expression);
        if (expected.TryGetProperty("weekdays", out var weekdays))
            Assert.Equal(weekdays.EnumerateArray().Select(value => value.GetInt32()), rule.Weekdays);
        if (!fixture.TryGetProperty("decision", out var decision)) return;
        if (fixture.TryGetProperty("pinned", out var pinned)) rule = rule with { ScheduledAt = Utc(pinned.GetString()!) };
        var occurrence = ScheduleRules.Occur(rule, Utc(decision.GetString()!));
        var upstreamOccurrence = fixture.GetProperty("occurrence");
        Assert.Equal(upstreamOccurrence.GetProperty("occurrenceAt").GetString(), ScheduleFraming.Instant(occurrence.OccurrenceAt));
        Assert.Equal(upstreamOccurrence.TryGetProperty("nextScheduledAt", out var next) ? next.GetString() : null,
            occurrence.NextAt is { } nextAt ? ScheduleFraming.Instant(nextAt) : null);
    }

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static ScheduleTiming Timing(string kind, JsonElement selector)
        => kind switch
        {
            "after" => new ScheduleTiming.After(selector.GetInt64()),
            "every" => new ScheduleTiming.Every(selector.GetInt64()),
            "at" when selector.ValueKind == JsonValueKind.String => new ScheduleTiming.AtInstant(selector.GetString()!),
            "at" => new ScheduleTiming.AtLocal(selector.GetProperty("date").GetString()!, selector.GetProperty("time").GetString()!, selector.GetProperty("time_zone").GetString()!),
            "daily" => new ScheduleTiming.Daily(selector.GetProperty("time").GetString()!, selector.GetProperty("time_zone").GetString()!),
            "weekly" => new ScheduleTiming.Weekly(selector.GetProperty("time").GetString()!, selector.GetProperty("time_zone").GetString()!,
                selector.GetProperty("weekdays").EnumerateArray().Select(value => value.GetInt32()).ToArray()),
            "cron" => new ScheduleTiming.Cron(selector.GetProperty("expression").GetString()!, selector.GetProperty("time_zone").GetString()!),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
