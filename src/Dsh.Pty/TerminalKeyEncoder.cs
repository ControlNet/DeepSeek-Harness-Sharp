using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Pty;

/**
 * 把窗口按键编码成 xterm 风格 VT 输入字节, 供 shell 窗格/图形终端写进 PTY。
 * 只做编码: 控制键、方向键/编辑键/F 键(带 xterm 修饰参数)、Ctrl 组合、Alt 的 ESC 前缀、可打印字符(含 CJK 的 UTF-8)。
 */
public static class TerminalKeyEncoder
{
    private const int BufferSize = 16;
    private const byte Escape = 0x1b;
    private const byte Delete = 0x7f;
    private const byte CtrlH = 0x08;

    /** xterm 修饰参数: 1 + Shift(1) + Alt(2) + Ctrl(4); 等于 1 表示没有修饰。 */
    private const int NoModifiers = 1;

    /** F5..F12 的 CSI 编号: xterm 在 F5/F6 与 F10/F11 之间各跳过一个编号。 */
    private static readonly int[] FunctionKeyCodes = [15, 17, 18, 19, 20, 21, 23, 24];

    /** 编码到 destination, 返回写入长度; 没有对应字节时返回 0。 */
    public static int Encode(ConsoleKeyInfo key, Span<byte> destination, bool applicationCursorKeys = false)
    {
        if (destination.Length < BufferSize)
            throw new ArgumentException($"destination must be at least {BufferSize} bytes", nameof(destination));

        var special = EncodeSpecial(key, destination, applicationCursorKeys);
        if (special >= 0)
            return special;

        if (IsAltGr(key))
            key = new ConsoleKeyInfo(key.KeyChar, key.Key, (key.Modifiers & ConsoleModifiers.Shift) != 0, alt: false, control: false);
        var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;
        var offset = alt ? 1 : 0;
        var length = EncodeBasic(key, destination[offset..]);
        if (length == 0)
            return 0;
        if (alt)
            destination[0] = Escape;
        return length + offset;
    }

    /** Windows 把 AltGr 报成 Ctrl+Alt: 产生了键面字母以外的可打印字符时按普通字符处理, 否则德语键盘 AltGr+Q 的 @ 会变成控制字节。 */
    private static bool IsAltGr(ConsoleKeyInfo key)
        => (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == (ConsoleModifiers.Control | ConsoleModifiers.Alt)
            && key.KeyChar >= ' '
            && !(key.Key is >= ConsoleKey.A and <= ConsoleKey.Z && char.ToUpperInvariant(key.KeyChar) == (char)key.Key);

    /** 方向键/编辑键/F 键; 不是这些键时返回 -1。修饰键编码进 CSI 参数而不是前缀。 */
    private static int EncodeSpecial(ConsoleKeyInfo key, Span<byte> destination, bool applicationCursorKeys)
    {
        var modifiers = ModifierParameter(key.Modifiers);
        return key.Key switch
        {
            ConsoleKey.UpArrow => Cursor('A', modifiers, destination, applicationCursorKeys),
            ConsoleKey.DownArrow => Cursor('B', modifiers, destination, applicationCursorKeys),
            ConsoleKey.RightArrow => Cursor('C', modifiers, destination, applicationCursorKeys),
            ConsoleKey.LeftArrow => Cursor('D', modifiers, destination, applicationCursorKeys),
            ConsoleKey.Home => Cursor('H', modifiers, destination, applicationCursorKeys),
            ConsoleKey.End => Cursor('F', modifiers, destination, applicationCursorKeys),
            ConsoleKey.Insert => Tilde(2, modifiers, destination),
            ConsoleKey.Delete => Tilde(3, modifiers, destination),
            ConsoleKey.PageUp => Tilde(5, modifiers, destination),
            ConsoleKey.PageDown => Tilde(6, modifiers, destination),
            >= ConsoleKey.F1 and <= ConsoleKey.F4 => Cursor((char)('P' + (key.Key - ConsoleKey.F1)), modifiers, destination, applicationCursorKeys: true),
            >= ConsoleKey.F5 and <= ConsoleKey.F12 => Tilde(FunctionKeyCodes[key.Key - ConsoleKey.F5], modifiers, destination),
            _ => -1,
        };
    }

    /** 不带 Alt 前缀的基础编码: Ctrl 组合、回车/退格/Tab/Esc/空格、可打印字符。 */
    private static int EncodeBasic(ConsoleKeyInfo key, Span<byte> destination)
    {
        var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        if (control && ControlByte(key) is { } controlByte)
        {
            destination[0] = controlByte;
            return 1;
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                destination[0] = (byte)'\r';
                return 1;
            case ConsoleKey.Tab when (key.Modifiers & ConsoleModifiers.Shift) != 0:
                return Write("\u001b[Z", destination);
            case ConsoleKey.Tab:
                destination[0] = (byte)'\t';
                return 1;
            case ConsoleKey.Backspace:
                destination[0] = control ? CtrlH : Delete;
                return 1;
            case ConsoleKey.Escape:
                destination[0] = Escape;
                return 1;
            case ConsoleKey.Spacebar:
                destination[0] = (byte)' ';
                return 1;
            default:
                break;
        }

        if (key.KeyChar == '\0')
            return 0;
        var character = key.KeyChar;
        return Encoding.UTF8.GetBytes(MemoryMarshal.CreateSpan(ref character, 1), destination);
    }

    /** Ctrl+字母 → 0x01..0x1A; Ctrl+空格/@ → NUL; Ctrl+[ \ ] ^ _ 及数字键上的 xterm 等价键 → 0x1B..0x1F, Ctrl+? → DEL。 */
    private static byte? ControlByte(ConsoleKeyInfo key)
    {
        if (key.Key is >= ConsoleKey.A and <= ConsoleKey.Z)
            return (byte)(key.Key - ConsoleKey.A + 1);
        if (key.Key == ConsoleKey.Spacebar)
            return 0;
        return key.KeyChar switch
        {
            '@' or ' ' or '2' => 0,
            '[' or '3' => Escape,
            '\\' or '4' => 0x1c,
            ']' or '5' => 0x1d,
            '^' or '6' => 0x1e,
            '_' or '-' or '/' or '7' => 0x1f,
            '?' or '8' => Delete,
            _ => null,
        };
    }

    private static int ModifierParameter(ConsoleModifiers modifiers)
        => NoModifiers
            + ((modifiers & ConsoleModifiers.Shift) != 0 ? 1 : 0)
            + ((modifiers & ConsoleModifiers.Alt) != 0 ? 2 : 0)
            + ((modifiers & ConsoleModifiers.Control) != 0 ? 4 : 0);

    /** 无修饰: 普通模式 `ESC [ X`, DECCKM 下 `ESC O X`; 有修饰: `ESC [ 1 ; m X`(与模式无关)。 */
    private static int Cursor(char final, int modifiers, Span<byte> destination, bool applicationCursorKeys)
    {
        if (modifiers != NoModifiers)
            return Write($"\u001b[1;{modifiers}{final}", destination);
        destination[0] = Escape;
        destination[1] = applicationCursorKeys ? (byte)'O' : (byte)'[';
        destination[2] = (byte)final;
        return 3;
    }

    private static int Tilde(int code, int modifiers, Span<byte> destination)
        => Write(modifiers == NoModifiers ? $"\u001b[{code}~" : $"\u001b[{code};{modifiers}~", destination);

    private static int Write(string text, Span<byte> destination)
        => Encoding.ASCII.GetBytes(text, destination);
}
