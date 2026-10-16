using System.Globalization;
using Dsh.Llm;
using Dsh.Schedule;

namespace Dsh.Tests;

/** Fixed cases from the pinned upstream daily/weekly/domain suites exercise the production calendar. */
public sealed class ScheduleClockCompatibilityTests
{
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Start = Utc("2026-01-01T00:00:00Z");

    [Theory]
    [InlineData("23:00:00.1", "23:00:00.100", "2026-01-01T23:00:00.100Z")]
    [InlineData("23:00:00.12", "23:00:00.120", "2026-01-01T23:00:00.120Z")]
    [InlineData("23:00:00.123", "23:00:00.123", "2026-01-01T23:00:00.123Z")]
    public void Daily_NormalizesMillisecondsWithoutLosingTheTarget(string time, string normalized, string expected)
    {
        var rule = ScheduleRules.Resolve(new ScheduleTiming.Daily(time, "UTC"), Start);
        Assert.Equal(normalized, rule.Time);
        Assert.Equal(expected, ScheduleFraming.Instant(rule.ScheduledAt));
        var due = ScheduleRules.Occur(rule, rule.ScheduledAt);
        Assert.Equal(rule.ScheduledAt, due.OccurrenceAt);
        Assert.Equal(rule.ScheduledAt.AddDays(1), due.NextAt);
    }

    [Theory]
    [InlineData("UTC", "UTC")]
    [InlineData("Etc/UTC", "UTC")]
    [InlineData("Etc/GMT", "UTC")]
    [InlineData("US/Eastern", "America/New_York")]
    [InlineData("Asia/Kolkata", "Asia/Calcutta")]
    [InlineData("Europe/Kyiv", "Europe/Kiev")]
    [InlineData("Europe/Guernsey", "Europe/Guernsey")]
    [InlineData("America/Argentina/Buenos_Aires", "America/Buenos_Aires")]
    [InlineData("Antarctica/South_Pole", "Antarctica/McMurdo")]
    [InlineData("america/new_york", "America/New_York")]
    public void Zones_AreCanonicalAcrossPlatforms(string input, string canonical)
        => Assert.Equal(canonical, ScheduleRules.Resolve(new ScheduleTiming.Daily("09:00:00", input), Start).TimeZone);

    [Theory]
    [InlineData("Eastern Standard Time")]
    [InlineData("GMT")]
    [InlineData("EST")]
    [InlineData("+08:00")]
    [InlineData(" UTC")]
    [InlineData("UTC ")]
    [InlineData("utc")]
    [InlineData("Mars/Olympus")]
    public void Zones_RejectNonIanaSelectors(string zone)
        => Assert.Equal(ScheduleErrorCodes.InvalidTimeZone,
            Assert.Throws<HarnessException>(() => ScheduleRules.Resolve(new ScheduleTiming.Daily("09:00:00", zone), Start)).Code);

    [Theory]
    [InlineData("2026-01-02T00:00:00.1+23:59", "2026-01-01T00:01:00.100Z")]
    [InlineData("2026-01-02T00:00:00.12-23:59", "2026-01-02T23:59:00.120Z")]
    [InlineData("2026-01-02T00:00:00.123+00:00", "2026-01-02T00:00:00.123Z")]
    public void OffsetInstant_AllowsTheUpstreamNumericOffsetRange(string input, string expected)
        => Assert.Equal(expected, ScheduleFraming.Instant(ScheduleRules.Resolve(new ScheduleTiming.AtInstant(input), Start).ScheduledAt));

    [Theory]
    [InlineData("0001-01-01T00:00:00+01:00")]
    [InlineData("9999-12-31T23:59:59.999-01:00")]
    public void OffsetInstant_RejectsUtcTargetsOutsideFourDigitYears(string input)
        => Assert.Equal(ScheduleErrorCodes.TimeOutOfRange,
            Assert.Throws<HarnessException>(() => ScheduleRules.Resolve(new ScheduleTiming.AtInstant(input), Start)).Code);

    [Theory]
    [InlineData("0001-01-01", "Etc/GMT-1")]
    [InlineData("9999-12-31", "Etc/GMT+1")]
    public void LocalInstant_DistinguishesRangeExhaustionFromAGap(string date, string zone)
        => Assert.Equal(ScheduleErrorCodes.TimeOutOfRange,
            Assert.Throws<HarnessException>(() => ScheduleRules.Resolve(new ScheduleTiming.AtLocal(date, date.StartsWith("0001", StringComparison.Ordinal) ? "00:00:00" : "23:59:59.999", zone), Start)).Code);

    [Theory]
    [InlineData("0001-01-01T00:00:00Z", "00:00:00", "Etc/GMT+1", "0001-01-01T01:00:00.000Z")]
    [InlineData("0001-01-01T00:00:00Z", "00:00:00", "Etc/GMT-1", "0001-01-01T23:00:00.000Z")]
    [InlineData("0001-01-01T00:00:00Z", "23:30:00", "Etc/GMT+1", "0001-01-01T00:30:00.000Z")]
    [InlineData("9999-12-31T23:00:00Z", "00:30:00", "Etc/GMT-1", "9999-12-31T23:30:00.000Z")]
    public void Daily_AcceptsLocalYearZeroAndTenThousandWhenUtcIsSupported(string now, string time, string zone, string target)
        => Assert.Equal(target, ScheduleFraming.Instant(ScheduleRules.Resolve(new ScheduleTiming.Daily(time, zone), Utc(now)).ScheduledAt));

    [Theory]
    [InlineData("0001-01-01T00:00:00Z", 1, "00:00:00", "Etc/GMT+1", "0001-01-01T01:00:00.000Z")]
    [InlineData("0001-01-01T00:00:00Z", 1, "00:00:00", "Etc/GMT-1", "0001-01-07T23:00:00.000Z")]
    [InlineData("9999-12-20T00:00:00Z", 7, "00:30:00", "Etc/GMT-1", "9999-12-25T23:30:00.000Z")]
    [InlineData("9999-12-20T00:00:00Z", 2, "00:30:00", "Etc/GMT-1", "9999-12-20T23:30:00.000Z")]
    public void Weekly_PreservesWeekdaysAtUtcYearBoundaries(string now, int weekday, string time, string zone, string target)
        => Assert.Equal(target, ScheduleFraming.Instant(ScheduleRules.Resolve(new ScheduleTiming.Weekly(time, zone, [weekday]), Utc(now)).ScheduledAt));

    [Fact]
    public void WholeDateGap_IsSkippedButLocalAtIsRejected()
    {
        var rule = ScheduleRules.Resolve(new ScheduleTiming.Daily("12:00:00", "Pacific/Apia"), Utc("2011-12-29T21:59:59Z"));
        Assert.Equal(Utc("2011-12-29T22:00:00Z"), rule.ScheduledAt);
        Assert.Equal(Utc("2011-12-30T22:00:00Z"), ScheduleRules.Occur(rule, rule.ScheduledAt).NextAt);
        Assert.Equal(ScheduleErrorCodes.InvalidRule,
            Assert.Throws<HarnessException>(() => ScheduleRules.Resolve(new ScheduleTiming.AtLocal("2011-12-30", "12:00:00", "Pacific/Apia"), Utc("2011-12-29T00:00:00Z"))).Code);
    }

    [Fact]
    public void DateLineRollback_SelectsTheLatestEarlierMappingAndConsumesItsDate()
    {
        var rule = ScheduleRules.Resolve(new ScheduleTiming.Daily("12:00:00", "America/Anchorage"), Utc("1867-10-17T00:00:00Z"));
        Assert.Equal(Utc("1867-10-17T21:59:36Z"), rule.ScheduledAt);
        var due = ScheduleRules.Occur(rule, Utc("1867-10-19T06:00:00Z"));
        Assert.Equal(Utc("1867-10-18T21:59:36Z"), due.OccurrenceAt);
        Assert.Equal(Utc("1867-10-20T21:59:36Z"), due.NextAt);
    }

    [Fact]
    public void PinnedDailyTarget_DoesNotRepeatItsLocalDate()
    {
        var rule = ScheduleRules.Resolve(new ScheduleTiming.Daily("23:00:00", "Asia/Shanghai"), Utc("2026-09-16T00:00:00Z"))
            with { ScheduledAt = Utc("2026-09-16T14:00:00Z") };
        var due = ScheduleRules.Occur(rule, Utc("2026-09-16T14:30:00Z"));
        Assert.Equal(rule.ScheduledAt, due.OccurrenceAt);
        Assert.Equal(Utc("2026-09-17T15:00:00Z"), due.NextAt);
    }

    [Fact]
    public void FinalRecurringOccurrences_AreRetainedWithoutOverflow()
    {
        var rule = ScheduleRules.Resolve(new ScheduleTiming.Daily("00:30:00", "Etc/GMT-1"), Utc("9999-12-31T23:00:00Z"));
        var due = ScheduleRules.Occur(rule, Utc("9999-12-31T23:59:59.999Z"));
        Assert.Equal(Utc("9999-12-31T23:30:00Z"), due.OccurrenceAt);
        Assert.Null(due.NextAt);
        Assert.Equal(ScheduleErrorCodes.FrequencyTooHigh,
            Assert.Throws<HarnessException>(() => ScheduleRules.Resolve(new ScheduleTiming.Every(0), Start)).Code);
    }
}
