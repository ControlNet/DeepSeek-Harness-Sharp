namespace Dsh.Tui;

/**
 * 判断"本进程是不是直接坐在一个真实控制台(VT)上"。
 *
 * 为什么需要: KMS/GBM 直连屏会**接管物理控制台**, 只有当我们自己的终端就是那个控制台时才该做;
 * 经 pts(SSH / tmux / 常驻 daemon 代理)进来时接管控制台, 用户在自己的 pts 里看不到画面, 只是白白占了别人的屏幕。
 *
 * 为什么不是"判断是不是 SSH 会话": 本工程的 TUI 常驻在 daemon 的 pty 里, 比 SSH 连接活得久(断连后仍在),
 * 所以"会话类型/SSH 是否还在"根本不是有效判据; 有效的是**当前状态**——stdin 现在指向 /dev/ttyN(VT)还是 /dev/pts/N。
 */
internal static class ConsoleTty
{
    /** Linux: /dev/ttyN(含 /dev/tty 本身)算控制台, /dev/pts/N、/dev/null、空/未知一律不算; 其它平台一律 false。 */
    public static bool IsDirectConsole
        => OperatingSystem.IsLinux() && IsConsoleDevice(StandardInputTarget());

    /** 判定函数与 IO 分离, 便于不依赖真实 tty 的用例覆盖。 */
    internal static bool IsConsoleDevice(string? target)
        => target is { Length: > 0 }
            && target.StartsWith("/dev/tty", StringComparison.Ordinal)
            && !target.StartsWith("/dev/pts/", StringComparison.Ordinal);

    private static string? StandardInputTarget()
    {
        try
        {
            return File.ResolveLinkTarget("/proc/self/fd/0", returnFinalTarget: true)?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
