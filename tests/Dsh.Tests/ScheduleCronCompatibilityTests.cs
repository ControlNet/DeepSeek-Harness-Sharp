using System.Globalization;
using Dsh.Llm;
using Dsh.Schedule;

namespace Dsh.Tests;

/** Fixtures from upstream 86a402200d95fecef4529d969836f1fe13c6cb0d cron.spec.ts and domain.ts. */
public sealed class ScheduleCronCompatibilityTests
{
    private static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    private static ScheduleRule Resolve(string expression, string floor = "2026-09-01T00:00:00Z", string zone = "UTC")
        => ScheduleRules.Resolve(new ScheduleTiming.Cron(expression, zone), Utc(floor));

    [Theory]
    [InlineData("0 0 * * *", "0 0 * * *")]
    [InlineData("*/15 9-17 * * 1-5", "*/15 9-17 * * 1-5")]
    [InlineData("30,10,20 * * * *", "10-30/10 * * * *")]
    [InlineData("20,30,10 * * * *", "10-30/10 * * * *")]
    [InlineData("0,0,30 * * * *", "0-30/30 * * * *")]
    [InlineData("*/1 * * * *", "* * * * *")]
    [InlineData("0 0 */2 * *", "0 0 */2 * *")]
    [InlineData("0-59/1 0 * * *", "0-59 0 * * *")]
    [InlineData("0-59 0-23 1-31 1-12 0-7", "0-59 0-23 1-31 1-12 0-6")]
    [InlineData("0 0 * * 0", "0 0 * * 0")]
    [InlineData("0 0 * * 7", "0 0 * * 0")]
    [InlineData("0 0 * * 1-2,3-4", "0 0 * * 1-4")]
    [InlineData("0 0 * * 5-7", "0 0 * * 0,5-6")]
    [InlineData("1,3,5 * * * *", "1-5/2 * * * *")]
    [InlineData("1-5/2 * * * *", "1-5/2 * * * *")]
    [InlineData("00 09 * * 5,4,3,2,1", "0 9 * * 1-5")]
    [InlineData("0 0 1-15/2 * *", "0 0 1-15/2 * *")]
    [InlineData("0 0 * * */7", "0 0 * * */7")]
    [InlineData("0 0 * * */3", "0 0 * * */3")]
    [InlineData("0 9 1-31 * */7", "0 9 1-31 * */7")]
    [InlineData("0 9 * * */7", "0 9 * * */7")]
    [InlineData("0 9 * * */2", "0 9 * * */2")]
    [InlineData("0 9 */31 * 1", "0 9 */31 * 1")]
    [InlineData("0 9 */2 * 1", "0 9 */2 * 1")]
    [InlineData("0 9 */2,4 * *", "0 9 */2,4 * *")]
    [InlineData("0 9 1-31/2 * 1", "0 9 1-31/2 * 1")]
    [InlineData("0 9 * * 1", "0 9 * * 1")]
    [InlineData("* * * * *", "* * * * *")]
    [InlineData("*,5 0 * * *", "* 0 * * *")]
    [InlineData("5,* 0 * * *", "0-59 0 * * *")]
    [InlineData("0 9 *,5 * 1", "0 9 * * 1")]
    [InlineData("0 9 5,* * 1", "0 9 1-31 * 1")]
    [InlineData("0 9 13 * *,5", "0 9 13 * *")]
    [InlineData("0 9 13 * 5,*", "0 9 13 * 0-6")]
    [InlineData("0 9 */30 * *", "0 9 */30 * *")]
    [InlineData("0 9 */4,3,7 * *", "0 9 */4,3-7/4 * *")]
    [InlineData("0 0 * * */4,2,6", "0 0 * * */2")]
    [InlineData("*/9007199254740991 0 * * *", "*/60 0 * * *")]
    [InlineData("0-59/9007199254740991 0 * * *", "0 0 * * *")]
    [InlineData("0 0 */9007199254740991 * *", "0 0 */31 * *")]
    [InlineData("0 0 * * */9007199254740991", "0 0 * * */7")]
    [InlineData("0000000000000000000000000000000000000000000000000000 0 * * *", "0 0 * * *")]
    [InlineData("0 0 * * 7,0,7", "0 0 * * 0")]
    [InlineData("1,2,4,5,6,9 * * * *", "1-2,4-6,9 * * * *")]
    [InlineData("0\t9\n*\r\n*\uFEFF1", "0 9 * * 1")]
    public void Canonicalization_PreservesUpstreamValueSetsAndStarFlags(string expression, string canonical)
    {
        var rule = Resolve(expression);
        Assert.Equal(canonical, rule.Expression);
        var stored = Resolve(canonical);
        Assert.Equal(canonical, stored.Expression);
        Assert.Equal(rule.ScheduledAt, stored.ScheduledAt);

        // Exercise stored/raw parsing through occurrence resolution, not a cloned test parser.
        var decision = rule.ScheduledAt.AddDays(8);
        var rawOccurrence = ScheduleRules.Occur(rule with { Expression = expression }, decision);
        Assert.Equal(rawOccurrence, ScheduleRules.Occur(rule, decision));
    }

    [Theory]
    [InlineData("0 0 * * * *")]
    [InlineData("0 0 * *")]
    [InlineData("@daily")]
    [InlineData("@yearly")]
    [InlineData("0 0 L * *")]
    [InlineData("0 0 15W * *")]
    [InlineData("0 0 * * 1#2")]
    [InlineData("0 0 * JAN *")]
    [InlineData("0 0 * * MON")]
    [InlineData("0 0 ? * *")]
    [InlineData("0 0 * * 5L")]
    [InlineData("60 0 * * *")]
    [InlineData("0 24 * * *")]
    [InlineData("0 0 0 * *")]
    [InlineData("0 0 32 * *")]
    [InlineData("0 0 * 0 *")]
    [InlineData("0 0 * 13 *")]
    [InlineData("0 0 * * 8")]
    [InlineData("0 0 20-10 * *")]
    [InlineData("0 0 * 12-1 *")]
    [InlineData("0 0 * * 7-0")]
    [InlineData("*/0 * * * *")]
    [InlineData("0-10/0 * * * *")]
    [InlineData("0,,1 * * * *")]
    [InlineData(",0 * * * *")]
    [InlineData("0 0 * * 1,")]
    [InlineData("5/2 * * * *")]
    [InlineData("*5 * * * *")]
    [InlineData("-5 * * * *")]
    [InlineData("+5 * * * *")]
    [InlineData("1.5 * * * *")]
    [InlineData("1e1 * * * *")]
    [InlineData("0x10 * * * *")]
    [InlineData("١ * * * *")]
    [InlineData("*/-1 * * * *")]
    [InlineData("*/+1 * * * *")]
    [InlineData("*/1.0 * * * *")]
    [InlineData("*/9007199254740992 * * * *")]
    [InlineData("0-59/9007199254740992 * * * *")]
    [InlineData("*/9999999999999999999999999999999999999999999 * * * *")]
    [InlineData("9007199254740991 * * * *")]
    [InlineData("0--2 * * * *")]
    [InlineData("0-1-2 * * * *")]
    [InlineData("0-2/1/2 * * * *")]
    [InlineData("0/1-2 * * * *")]
    [InlineData("*/ * * * *")]
    [InlineData(" 0 0 * * *")]
    [InlineData("0 0 * * * ")]
    [InlineData("\uFEFF0 0 * * *")]
    [InlineData("0 0 * * *\uFEFF")]
    [InlineData("0\u00859 * * *")]
    [InlineData("")]
    [InlineData(" ")]
    public void UnsupportedExpressions_AreInvalidRules(string expression)
    {
        var error = Assert.Throws<HarnessException>(() => Resolve(expression));
        Assert.Equal(ScheduleErrorCodes.InvalidRule, error.Code);
    }

    [Theory]
    [InlineData("2026-09-01T00:00:00Z", "0 0 13 * 5", "2026-09-04T00:00:00Z")]
    [InlineData("2026-09-12T00:00:00Z", "0 0 13 * 5", "2026-09-13T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 0 13 * *", "2026-09-13T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 0 * * 5", "2026-09-04T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 0 * * *", "2026-09-02T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 0 1-31 * 5", "2026-09-02T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 0 13 * 0-7", "2026-09-02T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 9 */2 * 1", "2026-09-07T09:00:00Z")]
    [InlineData("2026-09-13T00:00:00Z", "0 9 */2 * 1", "2026-09-21T09:00:00Z")]
    [InlineData("2026-09-02T00:00:00Z", "0 9 1-31 * */2", "2026-09-03T09:00:00Z")]
    [InlineData("2026-09-02T00:00:00Z", "0 9 */2 * */2", "2026-09-03T09:00:00Z")]
    [InlineData("2026-09-02T00:00:00Z", "0 9 */2 * *", "2026-09-03T09:00:00Z")]
    [InlineData("2026-09-02T00:00:00Z", "0 9 * * */2", "2026-09-03T09:00:00Z")]
    [InlineData("2026-09-13T00:00:00Z", "0 9 * * 1", "2026-09-14T09:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 9 1-31 * */7", "2026-09-06T09:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 9 1-31 * 0", "2026-09-01T09:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 9 *,5 * 1", "2026-09-07T09:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 9 5,* * 1", "2026-09-01T09:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 9 13 * *,5", "2026-09-13T09:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "0 9 13 * 5,*", "2026-09-01T09:00:00Z")]
    [InlineData("2026-01-01T00:00:00Z", "0 0 30 2 5", "2026-02-06T00:00:00Z")]
    [InlineData("2026-04-01T00:00:00Z", "0 0 31 * *", "2026-05-31T00:00:00Z")]
    [InlineData("2026-01-01T00:00:00Z", "0 0 29 2 *", "2028-02-29T00:00:00Z")]
    [InlineData("2096-02-29T00:00:00Z", "0 0 29 2 *", "2104-02-29T00:00:00Z")]
    [InlineData("2026-09-16T00:00:00Z", "0 0 * 1 *", "2027-01-01T00:00:00Z")]
    public void Targets_UseMonthAndVixieDaySemantics(string floor, string expression, string expected)
        => Assert.Equal(Utc(expected), Resolve(expression, floor).ScheduledAt);

    [Fact]
    public void Occurrence_PreservesStoredStarFlagForBothForwardAndBackwardSearches()
    {
        var starLed = Resolve("0 9 1-31 * */7");
        var restricted = starLed with { Expression = "0 9 1-31 * 0" };
        var decision = Utc("2026-09-07T12:00:00Z");
        Assert.Equal(new ScheduleOccurrence(Utc("2026-09-06T09:00:00Z"), Utc("2026-09-13T09:00:00Z")),
            ScheduleRules.Occur(starLed, decision));
        Assert.Equal(new ScheduleOccurrence(Utc("2026-09-07T09:00:00Z"), Utc("2026-09-08T09:00:00Z")),
            ScheduleRules.Occur(restricted, decision));
    }

    [Theory]
    [InlineData("2026-09-16T09:29:59.999Z", "2026-09-16T09:30:00Z")]
    [InlineData("2026-09-16T09:30:00Z", "2026-09-17T09:30:00Z")]
    [InlineData("2026-09-16T09:30:00.001Z", "2026-09-17T09:30:00Z")]
    public void Target_IsStrictlyFutureAtMinuteBoundaries(string floor, string expected)
        => Assert.Equal(Utc(expected), Resolve("30 9 * * *", floor).ScheduledAt);

    [Fact]
    public void HoursAndMinutes_AreSortedAndDeduplicatedBeforeOccurrenceSearch()
    {
        var rule = Resolve("45,0,15,15 17,9,9 * * *", "2026-09-15T09:15:00Z");
        Assert.Equal(Utc("2026-09-15T09:45:00Z"), rule.ScheduledAt);
        Assert.Equal(new ScheduleOccurrence(Utc("2026-09-15T17:15:00Z"), Utc("2026-09-15T17:45:00Z")),
            ScheduleRules.Occur(rule, Utc("2026-09-15T17:20:00Z")));
    }

    [Theory]
    [InlineData("1600-01-01T00:00:00Z")]
    [InlineData("2000-01-01T00:00:00Z")]
    [InlineData("2400-01-01T00:00:00Z")]
    [InlineData("9600-01-01T00:00:00Z")]
    public void ImpossibleFebruaryRule_ExhaustsTheGregorianFourHundredYearCycle(string floor)
    {
        var error = Assert.Throws<HarnessException>(() => Resolve("0 0 30 2 *", floor));
        Assert.Equal(ScheduleErrorCodes.TimeOutOfRange, error.Code);
        var pinned = new ScheduleRule(ScheduleKind.Cron, Utc(floor), TimeZone: "UTC", Expression: "0 0 30 2 *");
        Assert.Equal(new ScheduleOccurrence(pinned.ScheduledAt, null), ScheduleRules.Occur(pinned, Utc(floor).AddDays(1)));
    }

    [Fact]
    public void CronRecurrence_SkipsGapAndChoosesOnlyEarlierOverlap()
    {
        var gap = Resolve("30 2 * 3 0", "2026-03-02T00:00:00Z", "America/New_York");
        Assert.Equal(Utc("2026-03-15T06:30:00Z"), gap.ScheduledAt);
        var overlap = Resolve("30 1 * 11 0", "2026-10-25T00:00:00Z", "America/New_York");
        Assert.Equal(Utc("2026-11-01T05:30:00Z"), overlap.ScheduledAt);
        Assert.Equal(new ScheduleOccurrence(overlap.ScheduledAt, Utc("2026-11-08T06:30:00Z")),
            ScheduleRules.Occur(overlap, Utc("2026-11-01T06:30:00Z")));
    }
}
