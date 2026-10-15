namespace Dsh.Pty;

/** 终端的格子尺寸; 列在前, 避免与 PtySession.Resize(rows, columns) 的参数顺序混淆。 */
public readonly record struct TerminalSize(int Columns, int Rows);

/**
 * 终端视图与字节流来源之间的最小接口: 本地 PTY、远端 host.pty 流、外部 Agent 会话都以它接入同一个终端控件。
 * 所有权归调用方: 控件只读写与 resize, 不负责启动或终止对端。
 */
public interface ITerminalConnection
{
    /** 读子进程输出; 返回 0 表示流已结束。 */
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /** 写入键盘/粘贴/应答字节。 */
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /** 通知对端窗口尺寸变化(本地 PTY 即 TIOCSWINSZ/ResizePseudoConsole)。 */
    ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken);
}
