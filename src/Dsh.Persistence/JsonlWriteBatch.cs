namespace Dsh.Persistence;

/**
 * 流式 chunk 的写盘批次: 缓冲到 MaxEvents 条, 或最早一条缓冲满 Interval, 就写成一个帧并 fsync 一次。
 * 非 chunk 事件(消息、工具调用、轮次边界)连同之前缓冲的 chunk 立即落盘, 崩溃时最多丢失最后一批 chunk。
 */
public readonly record struct JsonlWriteBatch(int MaxEvents, TimeSpan Interval)
{
    public const int DefaultMaxEvents = 256;

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    public static JsonlWriteBatch Default => new(DefaultMaxEvents, DefaultInterval);
}
