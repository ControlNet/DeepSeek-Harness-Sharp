namespace Dsh.RemoteHost;

/** Optional remote terminal capabilities. */
public interface IRemotePty
{
    Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken = default);

    /** attach 后先收到整屏快照, 再收到实时输出; 多个客户端可同时 attach 同一个 PTY, 断开不影响 PTY。 */
    Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken = default);

    /** 调整远端 PTY 的尺寸，完成时 Host 已确认新几何。 */
    Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken = default);

    /** 终止远端某个 PTY。 */
    Task StopPtyAsync(string ptyId, CancellationToken cancellationToken = default);
}
