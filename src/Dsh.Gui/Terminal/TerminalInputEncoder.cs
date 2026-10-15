using System.Text;
using Avalonia.Input;
using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/**
 * 把 Avalonia 按键翻译成终端输入字节(编码规则复用 Dsh.Pty 的 TerminalKeyEncoder)。
 * 不带 Ctrl/Alt 的可打印键返回空, 交给 TextInput 事件发送, 输入法提交的文本也走那条路。
 */
public static class TerminalInputEncoder
{
    private const int EncodeBufferSize = 32;

    public static byte[] EncodeKey(Key key, KeyModifiers modifiers, string? keySymbol, bool applicationCursorKeys)
    {
        var control = (modifiers & KeyModifiers.Control) != 0;
        var alt = (modifiers & KeyModifiers.Alt) != 0;
        var shift = (modifiers & KeyModifiers.Shift) != 0;
        var consoleKey = SpecialKey(key);
        var keyChar = '\0';
        if (consoleKey is null)
        {
            if (!control && !alt)
                return [];
            keyChar = CharacterOf(key, keySymbol, shift);
            if (keyChar == '\0' || (control && alt && LetterKey(key) is null))
                return [];
            consoleKey = LetterKey(key) ?? (key == Key.Space ? ConsoleKey.Spacebar : ConsoleKey.NoName);
        }
        Span<byte> buffer = stackalloc byte[EncodeBufferSize];
        var length = TerminalKeyEncoder.Encode(new ConsoleKeyInfo(keyChar, consoleKey.Value, shift, alt, control), buffer, applicationCursorKeys);
        return buffer[..length].ToArray();
    }

    /** 文本通道(TextInput/输入法提交)的字节: 去掉控制字符, 它们已经由按键通道编码发送。 */
    public static byte[] EncodeText(string text)
    {
        var printable = string.Concat(text.Where(character => !char.IsControl(character)));
        return Encoding.UTF8.GetBytes(printable);
    }

    /** 不产生文本的功能键; 其余键返回 null。 */
    private static ConsoleKey? SpecialKey(Key key) => key switch
    {
        Key.Enter => ConsoleKey.Enter,
        Key.Tab => ConsoleKey.Tab,
        Key.Back => ConsoleKey.Backspace,
        Key.Escape => ConsoleKey.Escape,
        Key.Up => ConsoleKey.UpArrow,
        Key.Down => ConsoleKey.DownArrow,
        Key.Left => ConsoleKey.LeftArrow,
        Key.Right => ConsoleKey.RightArrow,
        Key.Home => ConsoleKey.Home,
        Key.End => ConsoleKey.End,
        Key.PageUp => ConsoleKey.PageUp,
        Key.PageDown => ConsoleKey.PageDown,
        Key.Insert => ConsoleKey.Insert,
        Key.Delete => ConsoleKey.Delete,
        >= Key.F1 and <= Key.F12 => ConsoleKey.F1 + (key - Key.F1),
        _ => null,
    };

    private static ConsoleKey? LetterKey(Key key)
        => key is >= Key.A and <= Key.Z ? ConsoleKey.A + (key - Key.A) : null;

    /** 组合键对应的字符: 优先用平台给的 KeySymbol(反映键盘布局), 拿不到或是控制字符时按键位推断字母/数字/空格。 */
    private static char CharacterOf(Key key, string? keySymbol, bool shift)
    {
        if (keySymbol is { Length: 1 } && !char.IsControl(keySymbol[0]))
            return keySymbol[0];
        return key switch
        {
            >= Key.A and <= Key.Z => (char)((shift ? 'A' : 'a') + (key - Key.A)),
            >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
            Key.Space => ' ',
            _ => '\0',
        };
    }
}
