using Dsh.Boot;
using Dsh.Host;

namespace Dsh.Tests;

/** systemd --user 单元的渲染: 不在测试里安装真实服务, 只验证单元会以当前二进制跑 host serve、停服务不带走 PTY daemon、路径按 systemd 规则转义。 */
public sealed class HostAutostartTests
{
    [Fact]
    public void Unit_RunsTheCurrentBinary_KeepsPtysOnStop_AndQuotesForSystemd()
    {
        var (executable, arguments) = HostAutostart.ServeCommand();

        var unit = HostAutostart.RenderUnit(executable, [.. arguments, "$x"], "/data/100% $HOME \"x\"");

        var lines = unit.Split('\n', StringSplitOptions.TrimEntries);
        Assert.Equal(["host", "serve"], arguments);
        Assert.Contains($"ExecStart=\"{executable}\" \"host\" \"serve\" \"$$x\"", lines);
        Assert.Contains($"Environment=\"{HarnessHome.HomeEnv}=/data/100%% $HOME \\\"x\\\"\"", lines);
        Assert.Contains("KillMode=process", lines);
        Assert.Contains("Restart=on-failure", lines);
        Assert.Contains("WantedBy=default.target", lines);
        Assert.Equal(HostAutostart.UnitName, Path.GetFileName(HostAutostart.UnitPath()));
    }

    [Fact]
    public void Unit_InheritsTheDefaultHomeWhenNoneIsGiven()
    {
        var (executable, arguments) = HostAutostart.ServeCommand();

        var unit = HostAutostart.RenderUnit(executable, arguments, null);

        Assert.DoesNotContain("Environment=", unit, StringComparison.Ordinal);
    }
}
