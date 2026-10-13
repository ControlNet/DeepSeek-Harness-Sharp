using System.Threading.Channels;

namespace Dsh.Pty;

/**
 * 窗格级订阅句柄: 从 PtySession.Subscribe 得到, 读取原始输出(首帧为当前屏快照), 释放即退订。
 * 与独占 attach 的区别是它不独占会话 —— 多个窗格可同时订阅同一会话, 会话本身继续正常运行。
 */
public sealed class PtySessionSubscription : IDisposable
{
    private readonly PtySession _session;
    private readonly Channel<byte[]> _channel;
    private bool _disposed;

    internal PtySessionSubscription(PtySession session, Channel<byte[]> channel)
    {
        _session = session;
        _channel = channel;
    }

    public ChannelReader<byte[]> Output => _channel.Reader;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.Unsubscribe(_channel);
    }
}
