using Dsh.Pty;

namespace Dsh.Tests;

/**
 * 负载下 daemon 的就绪/连接可能滞后(ConnectAsync 有 500ms 连接超时), 连接级异常会偶发;
 * 对这类"未就绪"异常做短暂重试, 而真实协议错误(如 unknown method)照常抛出。
 */
internal static class PtyDaemonRetry
{
    private const int MaxAttempts = 120;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(25);

    public static Task<IReadOnlyList<PtyDaemonSessionDto>> ListAsync(string socketPath, string? portFile, CancellationToken cancellationToken)
        => Async(() => PtyDaemonClient.ListAsync(socketPath, portFile, cancellationToken), cancellationToken);

    public static Task<PtyDaemonSessionDto> StartAsync(PtyDaemonStartParams parameters, string socketPath, string? portFile, CancellationToken cancellationToken)
        => Async(() => PtyDaemonClient.StartAsync(parameters, socketPath, portFile, cancellationToken), cancellationToken);

    public static Task<PtyRemoteSession> SubscribeAsync(string id, string socketPath, string? portFile, CancellationToken cancellationToken)
        => Async(() => PtyDaemonClient.SubscribeAsync(id, socketPath, portFile, cancellationToken), cancellationToken);

    public static Task<string> SnapshotAsync(string id, string socketPath, string? portFile, CancellationToken cancellationToken)
        => Async(() => PtyDaemonClient.SnapshotAsync(id, socketPath, portFile, cancellationToken), cancellationToken);

    public static async Task<T> Async<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception error) when (error is IOException or PtyDaemonNotRunningException && attempt < MaxAttempts)
            {
                await Task.Delay(Delay, cancellationToken);
            }
        }
    }
}
