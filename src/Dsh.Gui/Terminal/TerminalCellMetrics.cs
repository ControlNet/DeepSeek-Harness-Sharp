using Avalonia;
using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/** 一个字符格的像素尺寸, 以及据此在控件尺寸、格子数与指针位置之间换算。 */
public readonly record struct TerminalCellMetrics(double Width, double Height)
{
    public const int MinColumns = 2;
    public const int MinRows = 1;

    /** 浮点除法的容差: 780 / 7.8 可能得到 99.999…, 不加容差会少算一列。 */
    private const double Epsilon = 1e-6;

    /** 可用区域(扣掉内边距)能完整容纳的列数与行数, 至少 MinColumns x MinRows。 */
    public TerminalSize GridFor(Size available, Thickness padding)
    {
        var columns = (int)Math.Floor(((available.Width - padding.Left - padding.Right) / Width) + Epsilon);
        var rows = (int)Math.Floor(((available.Height - padding.Top - padding.Bottom) / Height) + Epsilon);
        return new TerminalSize(Math.Max(MinColumns, columns), Math.Max(MinRows, rows));
    }

    /** 容纳给定格子数所需的控件尺寸(含内边距)。 */
    public Size SizeFor(TerminalSize grid, Thickness padding)
        => new((grid.Columns * Width) + padding.Left + padding.Right, (grid.Rows * Height) + padding.Top + padding.Bottom);

    /** 指针位置所在的格子(0 起), 超出网格时贴到最近的边。 */
    public (int Column, int Row) CellAt(Point point, Thickness padding, TerminalSize grid)
    {
        var column = (int)Math.Floor((point.X - padding.Left) / Width);
        var row = (int)Math.Floor((point.Y - padding.Top) / Height);
        return (Math.Clamp(column, 0, grid.Columns - 1), Math.Clamp(row, 0, grid.Rows - 1));
    }

    public Rect CellRect(int column, int row, Thickness padding, int span = 1)
        => new(padding.Left + (column * Width), padding.Top + (row * Height), Width * span, Height);
}
