using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/** 一帧要画的内容: 在屏幕锁内从 VtScreen 拷出可见行与光标状态, 之后的排版绘制都在锁外进行。 */
public sealed class TerminalFrame
{
    private Cell[] _cells = [];

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int CursorColumn { get; private set; }

    public int CursorRow { get; private set; }

    /** 光标可见且视口停在底部(没有翻看滚回)。 */
    public bool CursorVisible { get; private set; }

    public TerminalCursorShape CursorShape { get; private set; }

    public int ScrollOffset { get; private set; }

    public ReadOnlySpan<Cell> Row(int y) => _cells.AsSpan(y * Columns, Columns);

    /** 拷出视口: scrollOffset 行滚回在上, 其余是当前屏; 调用方需持有屏幕锁。 */
    public void Capture(VtScreen screen, int scrollOffset)
    {
        (Columns, Rows, ScrollOffset) = (screen.Width, screen.Height, scrollOffset);
        if (_cells.Length != Columns * Rows)
            _cells = new Cell[Columns * Rows];
        for (var y = 0; y < Rows; y++)
        {
            var source = screen.LineAt(y - scrollOffset);
            var target = _cells.AsSpan(y * Columns, Columns);
            var copied = Math.Min(source.Length, Columns);
            source[..copied].CopyTo(target);
            target[copied..].Fill(new Cell(' '));
        }
        (CursorColumn, CursorRow, CursorShape) = (screen.CursorX, screen.CursorY + scrollOffset, screen.CursorShape);
        CursorVisible = screen.CursorVisible && scrollOffset == 0;
    }
}
