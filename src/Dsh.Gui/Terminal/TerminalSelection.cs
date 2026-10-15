using System.Text;
using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/** 选区端点: Line 相对屏幕顶行(负数是滚回), Column 是 0 起的列。 */
public readonly record struct TerminalCellPosition(int Line, int Column) : IComparable<TerminalCellPosition>
{
    public int CompareTo(TerminalCellPosition other)
        => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);
}

/** 按行流式(而非矩形)的文本选区, 两端都包含; Anchor 是按下处, Active 随拖动移动。 */
public readonly record struct TerminalSelection(TerminalCellPosition Anchor, TerminalCellPosition Active)
{
    public bool IsEmpty => Anchor == Active;

    public TerminalCellPosition Start => Anchor.CompareTo(Active) <= 0 ? Anchor : Active;

    public TerminalCellPosition End => Anchor.CompareTo(Active) <= 0 ? Active : Anchor;

    /** 某一行被选中的列区间(含两端); 该行不在选区内时返回 null。 */
    public (int First, int Last)? ColumnsOn(int line, int width)
    {
        var (start, end) = (Start, End);
        if (line < start.Line || line > end.Line)
            return null;
        var first = line == start.Line ? start.Column : 0;
        var last = line == end.Line ? end.Column : width - 1;
        return (Math.Clamp(first, 0, width - 1), Math.Clamp(last, 0, width - 1));
    }

    /** 取出选中文本: 跳过宽字符续格, 去掉每行行尾空白, 行间用 \n; 调用方需持有屏幕锁。 */
    public string ExtractText(VtScreen screen)
    {
        var builder = new StringBuilder();
        for (var line = Start.Line; line <= End.Line; line++)
        {
            var row = screen.LineAt(line);
            if (ColumnsOn(line, row.Length) is not (int first, int last))
                continue;
            var lineStart = builder.Length;
            foreach (var cell in row[first..(last + 1)])
            {
                if (cell.Character != '\0')
                    builder.Append(cell.Character);
            }
            TrimTrailingSpaces(builder, lineStart);
            if (line < End.Line)
                builder.Append('\n');
        }
        return builder.ToString();
    }

    private static void TrimTrailingSpaces(StringBuilder builder, int lineStart)
    {
        var length = builder.Length;
        while (length > lineStart && builder[length - 1] == ' ')
            length--;
        builder.Length = length;
    }
}
