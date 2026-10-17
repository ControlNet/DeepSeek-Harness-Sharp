namespace Dsh.Pty;

/** 子进程经 DECSET 申请的鼠标上报级别: 9 只报按下, 1000 报按下/松开/滚轮, 1002 加按住拖动, 1003 加任意移动。 */
public enum TerminalMouseTracking : byte
{
    None = 0,
    X10 = 1,
    Normal = 2,
    ButtonEvent = 3,
    AnyEvent = 4,
}

/** DECSCUSR 设定的光标形状; Default 由渲染方决定(通常是方块)。 */
public enum TerminalCursorShape : byte
{
    Default = 0,
    Block = 1,
    Underline = 2,
    Bar = 3,
}
