using Dsh.Llm;

namespace Dsh.Tests;

/** retry-after 响应头: 秒数或 HTTP 日期解析成毫秒, 重试等待优先采用供应商给出的时长并设上限。 */
public class RetryAfterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2", 2_000L)]
    [InlineData(" 1.5 ", 1_500L)]
    [InlineData("Thu, 15 Oct 2026 08:00:30 GMT", 30_000L)]
    [InlineData("0", null)]
    [InlineData("Thu, 15 Oct 2026 07:59:00 GMT", null)]
    [InlineData("soon", null)]
    [InlineData(null, null)]
    public void Header_Parses_Seconds_Or_Http_Date(string? header, long? expectedMs)
    {
        Assert.Equal(expectedMs, RetryAfterHeader.ParseMs(header, Now));
    }

    [Fact]
    public void Delay_Prefers_Provider_Hint_Within_Cap()
    {
        var policy = ResolvedRetryPolicy.Resolve(
            new RetryPolicyConfig.Normal(Backoff: new BackoffConfig(InitialDelayMs: 10, MaxDelayMs: 10, JitterRatio: 0)),
            "test");

        Assert.Equal(TimeSpan.FromMilliseconds(10), policy.Delay(0, null));
        Assert.Equal(TimeSpan.FromSeconds(3), policy.Delay(0, 3_000));
        Assert.Equal(TimeSpan.FromMilliseconds(ResolvedRetryPolicy.MaxProviderRetryAfterMs), policy.Delay(0, 3_600_000));
    }
}
