using System.Text;

namespace Dsh.Tests;

/** TerminalMouseEncoder: 上报级别过滤与 SGR/X10 两种编码。 */
public sealed class TerminalMouseEncoderTests
{
    [Theory]
    [InlineData(TerminalMouseTracking.None, TerminalMouseAction.Press, false, false)]
    [InlineData(TerminalMouseTracking.X10, TerminalMouseAction.Press, false, true)]
    [InlineData(TerminalMouseTracking.X10, TerminalMouseAction.Release, false, false)]
    [InlineData(TerminalMouseTracking.Normal, TerminalMouseAction.Release, false, true)]
    [InlineData(TerminalMouseTracking.Normal, TerminalMouseAction.Motion, true, false)]
    [InlineData(TerminalMouseTracking.ButtonEvent, TerminalMouseAction.Motion, true, true)]
    [InlineData(TerminalMouseTracking.ButtonEvent, TerminalMouseAction.Motion, false, false)]
    [InlineData(TerminalMouseTracking.AnyEvent, TerminalMouseAction.Motion, false, true)]
    public void Reports_Follows_Tracking_Level(TerminalMouseTracking tracking, TerminalMouseAction action, bool held, bool expected)
        => Assert.Equal(expected, TerminalMouseEncoder.Reports(tracking, action, held));

    [Fact]
    public void Sgr_Press_And_Release_Keep_The_Button()
    {
        var press = new TerminalMouseEvent(TerminalMouseAction.Press, TerminalMouseButton.Left, 4, 2);
        var release = press with { Action = TerminalMouseAction.Release };

        Assert.Equal("\e[<0;5;3M", Encoding.ASCII.GetString(TerminalMouseEncoder.Encode(press, sgr: true)));
        Assert.Equal("\e[<0;5;3m", Encoding.ASCII.GetString(TerminalMouseEncoder.Encode(release, sgr: true)));
    }

    [Fact]
    public void Sgr_Wheel_Drag_And_Modifiers()
    {
        var wheel = new TerminalMouseEvent(TerminalMouseAction.Press, TerminalMouseButton.WheelDown, 0, 0, ConsoleModifiers.Control);
        var drag = new TerminalMouseEvent(TerminalMouseAction.Motion, TerminalMouseButton.Left, 9, 9, ConsoleModifiers.Shift);

        Assert.Equal("\e[<81;1;1M", Encoding.ASCII.GetString(TerminalMouseEncoder.Encode(wheel, sgr: true)));
        Assert.Equal("\e[<36;10;10M", Encoding.ASCII.GetString(TerminalMouseEncoder.Encode(drag, sgr: true)));
    }

    [Fact]
    public void X10_Encodes_Bytes_And_Reports_Release_As_Button_3()
    {
        var release = new TerminalMouseEvent(TerminalMouseAction.Release, TerminalMouseButton.Right, 0, 1);

        Assert.Equal(new byte[] { 0x1b, (byte)'[', (byte)'M', 32 + 3, 32 + 1, 32 + 2 }, TerminalMouseEncoder.Encode(release, sgr: false));
    }

    [Fact]
    public void X10_Clamps_Coordinates_That_Do_Not_Fit_A_Byte()
    {
        var far = new TerminalMouseEvent(TerminalMouseAction.Press, TerminalMouseButton.Left, 500, 0);

        Assert.Equal(255, TerminalMouseEncoder.Encode(far, sgr: false)[4]);
    }
}
