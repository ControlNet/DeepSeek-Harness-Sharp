using System.Globalization;

namespace Dsh.Llm;

/** HTTP `retry-after` 响应头: 秒数(delay-seconds)或 HTTP 日期, 换算成从 now 起需要等待的毫秒数。 */
public static class RetryAfterHeader
{
    /** 无法解析、为零或已经过去的时间返回 null, 交给退避策略决定等待时长。 */
    public static long? ParseMs(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
            return Positive(seconds * 1000);
        if (DateTimeOffset.TryParseExact(trimmed, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return Positive((date - now).TotalMilliseconds);
        return null;
    }

    private static long? Positive(double milliseconds)
        => milliseconds >= 1 && double.IsFinite(milliseconds) ? (long)Math.Min(milliseconds, long.MaxValue) : null;
}
