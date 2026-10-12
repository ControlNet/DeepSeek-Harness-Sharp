using Dsh.Tui;

namespace Dsh.Tests;

/**
 * KMS/GBM 直连屏的守卫: 只有本进程的 stdin 是真实控制台(VT)时才接管物理控制台。
 * 判据与"是不是 SSH 会话"无关(本工程的 pty 比 SSH 活得久), 只看当前 stdin 指向哪里。
 */
public sealed class ConsoleTtyTests
{
    [Theory]
    [InlineData("/dev/tty1", true)]
    [InlineData("/dev/tty", true)]
    [InlineData("/dev/tty63", true)]
    [InlineData("/dev/pts/3", false)]
    [InlineData("/dev/pts/0", false)]
    [InlineData("/dev/null", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsConsoleDevice_OnlyTrueForVt(string? target, bool expected)
        => Assert.Equal(expected, ConsoleTty.IsConsoleDevice(target));
}
