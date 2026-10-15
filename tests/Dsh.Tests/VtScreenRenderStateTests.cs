using System.Text;

namespace Dsh.Tests;

/** VtScreen 给图形终端渲染与输入用的状态: 样式、标题、模式、查询应答, 以及全屏 TUI 依赖的控制序列。 */
public sealed class VtScreenRenderStateTests
{
    private static VtScreen Fed(string text, int width = 20, int height = 5)
    {
        var screen = new VtScreen(width, height);
        screen.Feed(Encoding.UTF8.GetBytes(text));
        return screen;
    }

    private static string RowText(VtScreen screen, int y) => Text(screen.Row(y));

    private static string Text(ReadOnlySpan<Cell> row)
    {
        var builder = new StringBuilder();
        foreach (var cell in row)
        {
            if (cell.Character != '\0')
                builder.Append(cell.Character);
        }
        return builder.ToString().TrimEnd();
    }

    [Fact]
    public void Sgr_Sets_And_Clears_Italic_Underline_Strikethrough()
    {
        var screen = Fed("\e[3;4;9mX\e[23;24;29mY");

        Assert.Equal(CellStyle.Italic | CellStyle.Underline | CellStyle.Strikethrough, screen.Row(0)[0].Style);
        Assert.Equal(CellStyle.None, screen.Row(0)[1].Style);
    }

    [Fact]
    public void Sgr_Colon_Subparameters_Set_Underline_Style_And_Truecolor()
    {
        var screen = Fed("\e[4:3mX\e[4:0mY\e[38:2::10:20:30mZ\e[48:5:196mW");

        Assert.Equal(CellStyle.Underline, screen.Row(0)[0].Style);
        Assert.Equal(CellStyle.None, screen.Row(0)[1].Style);
        Assert.Equal(CellColor.FromRgb(10, 20, 30), screen.Row(0)[2].Foreground);
        Assert.Equal(CellColor.FromRgb(255, 0, 0), screen.Row(0)[3].Background);
    }

    [Fact]
    public void Private_Prefixed_Sgr_Does_Not_Change_The_Pen()
    {
        // vim 启动时发 modifyOtherKeys(CSI > 4;2 m); 当成 SGR 会把后续文字全变粗体。
        var screen = Fed("\e[>4;2mX");

        Assert.Equal(CellStyle.None, screen.Row(0)[0].Style);
    }

    [Theory]
    [InlineData("\e]0;first title\a", "first title")]
    [InlineData("\e]2;second title\e\\", "second title")]
    public void Osc_Title_Is_Captured_With_Both_Terminators(string sequence, string expected)
    {
        var screen = Fed($"{sequence}after");

        Assert.Equal(expected, screen.Title);
        Assert.Equal("after", RowText(screen, 0));
    }

    [Fact]
    public void Alternate_Screen_Flag_Follows_Mode_1049()
    {
        var screen = Fed("\e[?1049h");
        Assert.True(screen.IsAlternateScreen);

        screen.Feed("\e[?1049l"u8);
        Assert.False(screen.IsAlternateScreen);
    }

    [Fact]
    public void Scroll_Region_Is_Exposed_Zero_Based()
    {
        var screen = Fed("\e[2;4r", height: 6);

        Assert.Equal(1, screen.ScrollTop);
        Assert.Equal(3, screen.ScrollBottom);
    }

    [Fact]
    public void Bracketed_Paste_And_Focus_Reporting_Modes_Toggle()
    {
        var screen = Fed("\e[?2004h\e[?1004h");
        Assert.True(screen.BracketedPaste);
        Assert.True(screen.FocusReporting);

        screen.Feed("\e[?2004l\e[?1004l"u8);
        Assert.False(screen.BracketedPaste);
        Assert.False(screen.FocusReporting);
    }

    [Theory]
    [InlineData("\e[?9h", TerminalMouseTracking.X10)]
    [InlineData("\e[?1000h", TerminalMouseTracking.Normal)]
    [InlineData("\e[?1002h", TerminalMouseTracking.ButtonEvent)]
    [InlineData("\e[?1003h", TerminalMouseTracking.AnyEvent)]
    [InlineData("\e[?1002h\e[?1002l", TerminalMouseTracking.None)]
    public void Mouse_Tracking_Mode_Follows_Decset(string sequence, TerminalMouseTracking expected)
        => Assert.Equal(expected, Fed(sequence).MouseTracking);

    [Fact]
    public void Sgr_Mouse_Encoding_Toggles()
    {
        var screen = Fed("\e[?1006h");
        Assert.True(screen.SgrMouse);

        screen.Feed("\e[?1006l"u8);
        Assert.False(screen.SgrMouse);
    }

    [Theory]
    [InlineData("\e[2 q", TerminalCursorShape.Block)]
    [InlineData("\e[4 q", TerminalCursorShape.Underline)]
    [InlineData("\e[6 q", TerminalCursorShape.Bar)]
    [InlineData("\e[6 q\e[0 q", TerminalCursorShape.Default)]
    public void Cursor_Shape_Follows_Decscusr(string sequence, TerminalCursorShape expected)
        => Assert.Equal(expected, Fed(sequence).CursorShape);

    [Fact]
    public void Cursor_Position_Report_Reflects_The_Cursor_At_Query_Time()
    {
        var screen = new VtScreen(20, 5);
        var replies = new List<string>();
        screen.Reply += replies.Add;

        screen.Feed("\e[3;7H\e[6nmoved"u8);

        Assert.Equal(["\e[3;7R"], replies);
    }

    [Fact]
    public void Device_Attribute_And_Status_Queries_Are_Answered()
    {
        var screen = new VtScreen(20, 5);
        var replies = new List<string>();
        screen.Reply += replies.Add;

        screen.Feed("\e[c\e[>c\e[5n"u8);

        Assert.Equal(3, replies.Count);
        Assert.StartsWith("\e[?", replies[0], StringComparison.Ordinal);
        Assert.EndsWith("c", replies[0], StringComparison.Ordinal);
        Assert.StartsWith("\e[>", replies[1], StringComparison.Ordinal);
        Assert.Equal("\e[0n", replies[2]);
    }

    [Fact]
    public void Color_Queries_Are_Answered_Only_When_Colors_Are_Known()
    {
        var screen = new VtScreen(20, 5);
        var replies = new List<string>();
        screen.Reply += replies.Add;

        screen.Feed("\e]11;?\a"u8);
        Assert.Empty(replies);

        screen.QueryForeground = CellColor.FromRgb(0xcc, 0xcc, 0xcc);
        screen.QueryBackground = CellColor.FromRgb(0x12, 0x34, 0x56);
        screen.Feed("\e]11;?\e\\\e]10;?\a"u8);
        Assert.Equal(["\e]11;rgb:1212/3434/5656\e\\", "\e]10;rgb:cccc/cccc/cccc\e\\"], replies);
    }

    [Theory]
    [InlineData("\eP+q544e\e\\X")]
    [InlineData("\e_Gf=24;AAAA\e\\X")]
    [InlineData("\e[>1u\e[?uX")]
    public void String_And_Keyboard_Protocol_Sequences_Do_Not_Print(string sequence)
    {
        var screen = Fed(sequence);

        Assert.Equal("X", RowText(screen, 0));
        Assert.Equal(1, screen.CursorX);
    }

    [Fact]
    public void Kitty_Keyboard_Query_Does_Not_Restore_The_Cursor()
    {
        var screen = Fed("\e[2;2H\e[s\e[4;6H\e[?u");

        Assert.Equal(5, screen.CursorX);
        Assert.Equal(3, screen.CursorY);
    }

    [Fact]
    public void Repeat_Preceding_Character_Expands()
    {
        Assert.Equal("aaaa", RowText(Fed("a\e[3b"), 0));
    }

    [Fact]
    public void Insert_Mode_Shifts_Existing_Text_Right()
    {
        Assert.Equal("Xabc", RowText(Fed("abc\r\e[4hX\e[4l"), 0));
    }

    [Fact]
    public void Alternate_Screen_Scrolling_Does_Not_Grow_Scrollback()
    {
        var screen = Fed("\e[?1049h" + string.Concat(Enumerable.Repeat("line\r\n", 10)), height: 3);

        Assert.Equal(0, screen.ScrollbackCount);
    }

    [Fact]
    public void Erase_Display_Mode_3_Clears_Scrollback_Only()
    {
        var screen = Fed(string.Concat(Enumerable.Repeat("line\r\n", 10)) + "keep", height: 3);
        Assert.True(screen.ScrollbackCount > 0);

        screen.Feed("\e[3J"u8);

        Assert.Equal(0, screen.ScrollbackCount);
        Assert.Equal("keep", RowText(screen, 2));
    }

    [Fact]
    public void Erase_Uses_The_Current_Background_Color()
    {
        var screen = Fed("\e[44m\e[2J");

        Assert.Equal(CellColor.FromPalette(AnsiColor.Blue), screen.Row(3)[10].Background);
    }

    [Fact]
    public void Shrinking_Keeps_The_Cursor_Line_And_Pushes_Top_Rows_To_Scrollback()
    {
        var screen = Fed("one\r\ntwo\r\nthree\r\nfour\r\nfive", height: 5);

        screen.Resize(20, 3);

        Assert.Equal("five", RowText(screen, 2));
        Assert.Equal(2, screen.CursorY);
        Assert.Equal(2, screen.ScrollbackCount);
        Assert.Equal("two", Text(screen.ScrollbackRow(0)));
    }

    [Fact]
    public void Zero_Width_Marks_And_Delete_Do_Not_Take_A_Cell()
    {
        Assert.Equal("ex", RowText(Fed("é‍\u007fx"), 0));
    }

    [Fact]
    public void Wide_Character_At_Last_Column_Wraps_To_Next_Line()
    {
        var screen = Fed("abc中", width: 4);

        Assert.Equal("abc", RowText(screen, 0));
        Assert.Equal('中', screen.Row(1)[0].Character);
        Assert.Equal('\0', screen.Row(1)[1].Character);
    }

    [Fact]
    public void Overwriting_Half_Of_A_Wide_Character_Clears_The_Other_Half()
    {
        var screen = Fed("中文\e[1;2HX");

        Assert.Equal(' ', screen.Row(0)[0].Character);
        Assert.Equal('X', screen.Row(0)[1].Character);
        Assert.Equal('文', screen.Row(0)[2].Character);
    }

    [Fact]
    public void LineAt_Indexes_Screen_From_Zero_And_Scrollback_Below_Zero()
    {
        var screen = Fed("old\r\nmid\r\nnew", height: 2);

        Assert.Equal("mid", Text(screen.LineAt(0)));
        Assert.Equal("new", Text(screen.LineAt(1)));
        Assert.Equal("old", Text(screen.LineAt(-1)));
    }
}
