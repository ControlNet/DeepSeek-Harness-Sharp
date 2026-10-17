using System.Text;
using Avalonia.Input;
using Dsh.Gui.Terminal;

namespace Dsh.Tests;

/** TerminalInputEncoder: Avalonia 按键到终端字节; 可打印键留给 TextInput 通道。 */
public sealed class TerminalInputEncoderTests
{
    private static string Encode(Key key, KeyModifiers modifiers = KeyModifiers.None, string? symbol = null, bool applicationCursorKeys = false)
        => Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(key, modifiers, symbol, applicationCursorKeys));

    [Theory]
    [InlineData(Key.A, "a")]
    [InlineData(Key.D1, "1")]
    [InlineData(Key.Space, " ")]
    [InlineData(Key.OemComma, ",")]
    public void Printable_Keys_Without_Modifiers_Go_To_Text_Input(Key key, string symbol)
        => Assert.Empty(TerminalInputEncoder.EncodeKey(key, KeyModifiers.None, symbol, applicationCursorKeys: false));

    [Theory]
    [InlineData(Key.Enter, "\r")]
    [InlineData(Key.Back, "\x7f")]
    [InlineData(Key.Tab, "\t")]
    [InlineData(Key.Escape, "\e")]
    [InlineData(Key.Up, "\e[A")]
    [InlineData(Key.Down, "\e[B")]
    [InlineData(Key.Right, "\e[C")]
    [InlineData(Key.Left, "\e[D")]
    [InlineData(Key.Home, "\e[H")]
    [InlineData(Key.End, "\e[F")]
    [InlineData(Key.PageUp, "\e[5~")]
    [InlineData(Key.PageDown, "\e[6~")]
    [InlineData(Key.Insert, "\e[2~")]
    [InlineData(Key.Delete, "\e[3~")]
    [InlineData(Key.F1, "\eOP")]
    [InlineData(Key.F12, "\e[24~")]
    public void Function_Keys(Key key, string expected)
        => Assert.Equal(expected, Encode(key));

    [Theory]
    [InlineData(Key.Up, "\eOA")]
    [InlineData(Key.Left, "\eOD")]
    [InlineData(Key.Home, "\eOH")]
    public void Cursor_Keys_In_Application_Mode(Key key, string expected)
        => Assert.Equal(expected, Encode(key, applicationCursorKeys: true));

    [Theory]
    [InlineData(Key.C, "c")]
    [InlineData(Key.C, "\u0003")]
    [InlineData(Key.C, null)]
    public void Ctrl_Letter_Is_A_Control_Byte_Whatever_The_Platform_Symbol(Key key, string? symbol)
        => Assert.Equal("\x03", Encode(key, KeyModifiers.Control, symbol));

    [Fact]
    public void Ctrl_Bracket_Is_Escape()
        => Assert.Equal("\e", Encode(Key.OemOpenBrackets, KeyModifiers.Control, "["));

    [Fact]
    public void Ctrl_Space_Is_Nul()
        => Assert.Equal("\0", Encode(Key.Space, KeyModifiers.Control, " "));

    [Theory]
    [InlineData(Key.B, KeyModifiers.Alt, "b", "\eb")]
    [InlineData(Key.B, KeyModifiers.Alt | KeyModifiers.Shift, "B", "\eB")]
    [InlineData(Key.OemPeriod, KeyModifiers.Alt, ".", "\e.")]
    [InlineData(Key.Back, KeyModifiers.Alt, null, "\e\x7f")]
    public void Alt_Prefixes_Escape(Key key, KeyModifiers modifiers, string? symbol, string expected)
        => Assert.Equal(expected, Encode(key, modifiers, symbol));

    [Fact]
    public void Ctrl_Arrow_Carries_Modifier_Parameter()
        => Assert.Equal("\e[1;5C", Encode(Key.Right, KeyModifiers.Control, applicationCursorKeys: true));

    [Fact]
    public void Shift_Tab_Is_Back_Tab()
        => Assert.Equal("\e[Z", Encode(Key.Tab, KeyModifiers.Shift));

    [Fact]
    public void AltGr_Symbol_Goes_To_Text_Input()
        => Assert.Empty(TerminalInputEncoder.EncodeKey(Key.D2, KeyModifiers.Control | KeyModifiers.Alt, "@", applicationCursorKeys: false));

    [Fact]
    public void Committed_Text_Keeps_Cjk_And_Drops_Control_Characters()
        => Assert.Equal("中文ok", Encoding.UTF8.GetString(TerminalInputEncoder.EncodeText("中文\rok\u0003")));
}
