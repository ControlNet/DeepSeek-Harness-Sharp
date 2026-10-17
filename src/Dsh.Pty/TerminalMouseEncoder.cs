using System.Text;

namespace Dsh.Pty;

/** 上报给子进程的鼠标按钮编码; 数值即 xterm 协议里的按钮码。 */
public enum TerminalMouseButton : byte
{
    Left = 0,
    Middle = 1,
    Right = 2,
    None = 3,
    WheelUp = 64,
    WheelDown = 65,
}

public enum TerminalMouseAction : byte
{
    Press,
    Release,
    Motion,
}

/** 一次鼠标事件; Column/Row 是 0 起的格坐标。 */
public readonly record struct TerminalMouseEvent(
    TerminalMouseAction Action,
    TerminalMouseButton Button,
    int Column,
    int Row,
    ConsoleModifiers Modifiers = 0);

/** 按子进程申请的上报级别过滤鼠标事件, 并编码成 SGR(`?1006`)或 X10 字节序列。 */
public static class TerminalMouseEncoder
{
    private const int MotionFlag = 32;
    private const int ShiftFlag = 4;
    private const int AltFlag = 8;
    private const int ControlFlag = 16;

    /** X10 编码把坐标和按钮码加 32 后放进单字节, 超过 255 的坐标无法表示, 只能贴边。 */
    private const int X10Offset = 32;
    private const int X10MaxCoordinate = 255 - X10Offset;

    /** 当前级别是否上报该事件: X10 只报按下, 1000 报按下松开, 1002 再报按住拖动, 1003 报一切移动。 */
    public static bool Reports(TerminalMouseTracking tracking, TerminalMouseAction action, bool buttonHeld) => tracking switch
    {
        TerminalMouseTracking.X10 => action == TerminalMouseAction.Press,
        TerminalMouseTracking.Normal => action != TerminalMouseAction.Motion,
        TerminalMouseTracking.ButtonEvent => action != TerminalMouseAction.Motion || buttonHeld,
        TerminalMouseTracking.AnyEvent => true,
        _ => false,
    };

    public static byte[] Encode(TerminalMouseEvent mouse, bool sgr)
    {
        var code = ButtonCode(mouse, sgr);
        if (sgr)
            return Encoding.ASCII.GetBytes($"\u001b[<{code};{mouse.Column + 1};{mouse.Row + 1}{(mouse.Action == TerminalMouseAction.Release ? 'm' : 'M')}");
        return
        [
            0x1b, (byte)'[', (byte)'M',
            (byte)(X10Offset + code),
            (byte)(X10Offset + Math.Clamp(mouse.Column + 1, 1, X10MaxCoordinate)),
            (byte)(X10Offset + Math.Clamp(mouse.Row + 1, 1, X10MaxCoordinate)),
        ];
    }

    /** X10 编码的松开不带按钮号(统一报 3), SGR 用终止符 m 区分松开并保留按钮号。 */
    private static int ButtonCode(TerminalMouseEvent mouse, bool sgr)
    {
        var code = mouse.Action == TerminalMouseAction.Release && !sgr ? (int)TerminalMouseButton.None : (int)mouse.Button;
        if (mouse.Action == TerminalMouseAction.Motion)
            code += MotionFlag;
        if ((mouse.Modifiers & ConsoleModifiers.Shift) != 0)
            code += ShiftFlag;
        if ((mouse.Modifiers & ConsoleModifiers.Alt) != 0)
            code += AltFlag;
        if ((mouse.Modifiers & ConsoleModifiers.Control) != 0)
            code += ControlFlag;
        return code;
    }
}
