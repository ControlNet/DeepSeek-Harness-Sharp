using System.Text;

namespace Dsh.Tests;

/** TerminalKeyEncoder: 控制键、带 xterm 修饰参数的方向/编辑/F 键、Ctrl 组合与 Alt 前缀。 */
public sealed class TerminalKeyEncoderTests
{
    private static string Encode(ConsoleKey key, char keyChar = '\0', ConsoleModifiers modifiers = 0, bool applicationCursorKeys = false)
    {
        var info = new ConsoleKeyInfo(
            keyChar,
            key,
            (modifiers & ConsoleModifiers.Shift) != 0,
            (modifiers & ConsoleModifiers.Alt) != 0,
            (modifiers & ConsoleModifiers.Control) != 0);
        Span<byte> buffer = stackalloc byte[32];
        var length = TerminalKeyEncoder.Encode(info, buffer, applicationCursorKeys);
        return Encoding.UTF8.GetString(buffer[..length]);
    }

    [Theory]
    [InlineData(ConsoleKey.Enter, "\r")]
    [InlineData(ConsoleKey.Backspace, "\x7f")]
    [InlineData(ConsoleKey.Tab, "\t")]
    [InlineData(ConsoleKey.Escape, "\e")]
    [InlineData(ConsoleKey.Insert, "\e[2~")]
    [InlineData(ConsoleKey.Delete, "\e[3~")]
    [InlineData(ConsoleKey.PageUp, "\e[5~")]
    [InlineData(ConsoleKey.PageDown, "\e[6~")]
    [InlineData(ConsoleKey.F1, "\eOP")]
    [InlineData(ConsoleKey.F4, "\eOS")]
    [InlineData(ConsoleKey.F5, "\e[15~")]
    [InlineData(ConsoleKey.F6, "\e[17~")]
    [InlineData(ConsoleKey.F10, "\e[21~")]
    [InlineData(ConsoleKey.F11, "\e[23~")]
    [InlineData(ConsoleKey.F12, "\e[24~")]
    public void Unmodified_Special_Keys(ConsoleKey key, string expected)
        => Assert.Equal(expected, Encode(key));

    [Theory]
    [InlineData(ConsoleKey.UpArrow, false, "\e[A")]
    [InlineData(ConsoleKey.UpArrow, true, "\eOA")]
    [InlineData(ConsoleKey.LeftArrow, true, "\eOD")]
    [InlineData(ConsoleKey.Home, false, "\e[H")]
    [InlineData(ConsoleKey.End, true, "\eOF")]
    public void Cursor_Keys_Follow_Decckm(ConsoleKey key, bool applicationCursorKeys, string expected)
        => Assert.Equal(expected, Encode(key, applicationCursorKeys: applicationCursorKeys));

    [Theory]
    [InlineData(ConsoleKey.RightArrow, ConsoleModifiers.Control, "\e[1;5C")]
    [InlineData(ConsoleKey.UpArrow, ConsoleModifiers.Shift, "\e[1;2A")]
    [InlineData(ConsoleKey.LeftArrow, ConsoleModifiers.Alt, "\e[1;3D")]
    [InlineData(ConsoleKey.Delete, ConsoleModifiers.Control, "\e[3;5~")]
    [InlineData(ConsoleKey.F2, ConsoleModifiers.Shift, "\e[1;2Q")]
    [InlineData(ConsoleKey.F5, ConsoleModifiers.Control | ConsoleModifiers.Shift, "\e[15;6~")]
    public void Modified_Special_Keys_Carry_The_Xterm_Parameter(ConsoleKey key, ConsoleModifiers modifiers, string expected)
        => Assert.Equal(expected, Encode(key, modifiers: modifiers, applicationCursorKeys: true));

    [Theory]
    [InlineData(ConsoleKey.A, 'a', "\x01")]
    [InlineData(ConsoleKey.C, 'c', "\x03")]
    [InlineData(ConsoleKey.Z, 'z', "\x1a")]
    [InlineData(ConsoleKey.Spacebar, ' ', "\0")]
    [InlineData(ConsoleKey.Oem4, '[', "\e")]
    [InlineData(ConsoleKey.Oem5, '\\', "\x1c")]
    [InlineData(ConsoleKey.Oem6, ']', "\x1d")]
    [InlineData(ConsoleKey.Oem2, '/', "\x1f")]
    [InlineData(ConsoleKey.Backspace, '\0', "\b")]
    public void Control_Combinations(ConsoleKey key, char keyChar, string expected)
        => Assert.Equal(expected, Encode(key, keyChar, ConsoleModifiers.Control));

    [Theory]
    [InlineData(ConsoleKey.B, 'b', ConsoleModifiers.Alt, "\eb")]
    [InlineData(ConsoleKey.B, 'B', ConsoleModifiers.Alt | ConsoleModifiers.Shift, "\eB")]
    [InlineData(ConsoleKey.Backspace, '\0', ConsoleModifiers.Alt, "\e\x7f")]
    [InlineData(ConsoleKey.Enter, '\0', ConsoleModifiers.Alt, "\e\r")]
    [InlineData(ConsoleKey.X, 'x', ConsoleModifiers.Alt | ConsoleModifiers.Control, "\e\x18")]
    public void Alt_Prefixes_Escape(ConsoleKey key, char keyChar, ConsoleModifiers modifiers, string expected)
        => Assert.Equal(expected, Encode(key, keyChar, modifiers));

    [Fact]
    public void AltGr_Character_Is_Sent_As_Text()
        => Assert.Equal("@", Encode(ConsoleKey.Q, '@', ConsoleModifiers.Control | ConsoleModifiers.Alt));

    [Fact]
    public void Shift_Tab_Is_Back_Tab()
        => Assert.Equal("\e[Z", Encode(ConsoleKey.Tab, modifiers: ConsoleModifiers.Shift));

    [Fact]
    public void Printable_Cjk_Is_Utf8()
        => Assert.Equal("中", Encode(ConsoleKey.NoName, '中'));
}
