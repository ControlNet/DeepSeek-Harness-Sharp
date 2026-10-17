using Avalonia;
using Dsh.Gui.Terminal;

namespace Dsh.Tests;

/** TerminalCellMetrics: 控件尺寸、格子数与指针位置之间的换算。 */
public sealed class TerminalCellMetricsTests
{
    private static readonly TerminalCellMetrics Metrics = new(8, 16);
    private static readonly Thickness Padding = new(6);

    [Fact]
    public void Grid_Counts_Whole_Cells_Inside_Padding()
        => Assert.Equal(new TerminalSize(100, 30), Metrics.GridFor(new Size(812 + 7, 492 + 15), Padding));

    [Fact]
    public void Grid_Is_Not_Lost_To_Floating_Point_Error()
        => Assert.Equal(new TerminalSize(100, 10), new TerminalCellMetrics(7.8, 15.6).GridFor(new Size(780, 156), default));

    [Fact]
    public void Grid_Has_A_Minimum_Size()
        => Assert.Equal(new TerminalSize(TerminalCellMetrics.MinColumns, TerminalCellMetrics.MinRows), Metrics.GridFor(new Size(3, 3), Padding));

    [Fact]
    public void Size_For_Grid_Round_Trips()
    {
        var grid = new TerminalSize(120, 40);

        Assert.Equal(grid, Metrics.GridFor(Metrics.SizeFor(grid, Padding), Padding));
    }

    [Theory]
    [InlineData(6, 6, 0, 0)]
    [InlineData(6 + 8 * 3 + 7.9, 6 + 16 * 2 + 1, 3, 2)]
    [InlineData(-50, -50, 0, 0)]
    [InlineData(5000, 5000, 79, 23)]
    public void Cell_At_Point_Clamps_To_The_Grid(double x, double y, int column, int row)
        => Assert.Equal((column, row), Metrics.CellAt(new Point(x, y), Padding, new TerminalSize(80, 24)));

    [Fact]
    public void Cell_Rect_Spans_Wide_Cells()
        => Assert.Equal(new Rect(6 + 16, 6 + 32, 16, 16), Metrics.CellRect(2, 2, Padding, span: 2));
}
