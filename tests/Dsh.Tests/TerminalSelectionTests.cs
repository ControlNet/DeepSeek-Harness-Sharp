using System.Text;
using Dsh.Gui.Terminal;

namespace Dsh.Tests;

/** TerminalSelection: 行流式选区的列区间与从 VtScreen 取文本。 */
public sealed class TerminalSelectionTests
{
    private static VtScreen Screen(string text, int width = 12, int height = 4)
    {
        var screen = new VtScreen(width, height);
        screen.Feed(Encoding.UTF8.GetBytes(text));
        return screen;
    }

    [Fact]
    public void Multi_Line_Selection_Trims_Trailing_Spaces()
    {
        var screen = Screen("first line\r\nsecond\r\nthird");
        var selection = new TerminalSelection(new TerminalCellPosition(0, 6), new TerminalCellPosition(2, 2));

        Assert.Equal("line\nsecond\nthi", selection.ExtractText(screen));
    }

    [Fact]
    public void Reversed_Drag_Selects_The_Same_Text()
    {
        var screen = Screen("abcdef");
        var forward = new TerminalSelection(new TerminalCellPosition(0, 1), new TerminalCellPosition(0, 3));
        var backward = new TerminalSelection(new TerminalCellPosition(0, 3), new TerminalCellPosition(0, 1));

        Assert.Equal("bcd", forward.ExtractText(screen));
        Assert.Equal(forward.ExtractText(screen), backward.ExtractText(screen));
    }

    [Fact]
    public void Wide_Characters_Are_Copied_Once()
    {
        var screen = Screen("a中文b");
        var selection = new TerminalSelection(new TerminalCellPosition(0, 0), new TerminalCellPosition(0, 5));

        Assert.Equal("a中文b", selection.ExtractText(screen));
    }

    [Fact]
    public void Selection_Reaches_Into_Scrollback()
    {
        var screen = Screen("old\r\nl1\r\nl2\r\nl3\r\nl4", height: 4);
        var selection = new TerminalSelection(new TerminalCellPosition(-1, 0), new TerminalCellPosition(0, 1));

        Assert.Equal("old\nl1", selection.ExtractText(screen));
    }

    [Fact]
    public void Columns_On_Covers_Middle_Lines_Fully()
    {
        var selection = new TerminalSelection(new TerminalCellPosition(1, 4), new TerminalCellPosition(3, 2));

        Assert.Null(selection.ColumnsOn(0, 10));
        Assert.Equal((4, 9), selection.ColumnsOn(1, 10));
        Assert.Equal((0, 9), selection.ColumnsOn(2, 10));
        Assert.Equal((0, 2), selection.ColumnsOn(3, 10));
    }
}
