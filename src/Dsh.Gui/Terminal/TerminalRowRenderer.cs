using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/**
 * 逐行把单元格排成绘制指令: 背景色块、按(字体, 颜色)分段的 GlyphRun、下划线/删除线。
 * 每个字形的前进宽度显式设为格宽(宽字符两格), 回退字体的字形也严格落在网格上; 行内容没变时复用上一帧的排版结果。
 */
public sealed class TerminalRowRenderer
{
    private const char Space = ' ';

    /** 孤立的代理码元(配对的另一半被覆盖)无法成字, 画成替换字符。 */
    private const int ReplacementCharacter = 0xFFFD;

    private readonly List<RowVisual> _rows = [];
    private TerminalFontSet? _fonts;

    public void Reset(TerminalFontSet fonts)
    {
        _fonts = fonts;
        foreach (var row in _rows)
            row.Clear();
        _rows.Clear();
    }

    public void Draw(DrawingContext context, TerminalFrame frame, Thickness padding)
    {
        if (_fonts is not { } fonts)
            return;
        while (_rows.Count < frame.Rows)
            _rows.Add(new RowVisual());
        for (var y = 0; y < frame.Rows; y++)
        {
            var row = frame.Row(y);
            var visual = _rows[y];
            if (!row.SequenceEqual(visual.Cells))
                Build(visual, row, fonts);
            using (context.PushTransform(Matrix.CreateTranslation(padding.Left, padding.Top + (y * fonts.Metrics.Height))))
                visual.Draw(context);
        }
    }

    /** 单个格子的字形(光标处反色重画用); 空白或续格返回 null。 */
    public GlyphRun? BuildCellGlyph(Cell cell, Point cellOrigin)
    {
        if (_fonts is not { } fonts || cell.Character is Space or '\0' || char.IsSurrogate(cell.Character))
            return null;
        var span = TerminalTextWidth.Of(cell.Character);
        var glyph = fonts.Resolve(cell.Character, cell.Style);
        var info = new GlyphInfo(glyph.Index, 0, span * fonts.Metrics.Width, new Vector(CenterOffset(glyph, span, fonts), 0));
        return new GlyphRun(glyph.Typeface, fonts.Size, new[] { cell.Character }, [info], new Point(cellOrigin.X, cellOrigin.Y + fonts.Baseline));
    }

    private static void Build(RowVisual visual, ReadOnlySpan<Cell> row, TerminalFontSet fonts)
    {
        visual.Clear();
        visual.Cells = row.ToArray();
        var text = new TextRunBuilder(fonts, visual);
        for (var column = 0; column < row.Length; column++)
        {
            var cell = row[column];
            if (cell.Character == '\0')
                continue;
            var (codepoint, span) = CodepointAt(row, column);
            var (foreground, background) = TerminalPalette.ColorsOf(cell);
            var left = column * fonts.Metrics.Width;
            var width = Math.Min(span, row.Length - column) * fonts.Metrics.Width;
            if (background != TerminalPalette.DefaultBackground)
                visual.AddBackground(new Rect(left, 0, width, fonts.Metrics.Height), TerminalPalette.BrushOf(background));
            if (cell.Character != Space)
                text.Add(codepoint, fonts.Resolve(codepoint, cell.Style), left, span, TerminalPalette.BrushOf(foreground));
            AddDecorations(visual, cell.Style, left, width, fonts, TerminalPalette.BrushOf(foreground));
            column += char.IsHighSurrogate(cell.Character) && span == 2 ? 1 : 0;
        }
        text.Flush();
    }

    /** 码点与占格数: 代理对存在相邻两格里(VtScreen 每格只存一个 char), 合成一个码点占两格; 其余按东亚宽度。 */
    private static (int Codepoint, int Span) CodepointAt(ReadOnlySpan<Cell> row, int column)
    {
        var character = row[column].Character;
        if (char.IsHighSurrogate(character) && column + 1 < row.Length && char.IsLowSurrogate(row[column + 1].Character))
            return (char.ConvertToUtf32(character, row[column + 1].Character), 2);
        return char.IsSurrogate(character) ? (ReplacementCharacter, 1) : (character, TerminalTextWidth.Of(character));
    }

    private static void AddDecorations(RowVisual visual, CellStyle style, double left, double width, TerminalFontSet fonts, IBrush brush)
    {
        if ((style & CellStyle.Underline) != 0)
            visual.AddLine(new Rect(left, fonts.Baseline + fonts.UnderlineOffset, width, fonts.LineThickness), brush);
        if ((style & CellStyle.Strikethrough) != 0)
            visual.AddLine(new Rect(left, fonts.Baseline * 2 / 3, width, fonts.LineThickness), brush);
    }

    /** 字形比所占格子窄(中文字形通常窄于两个英文格)时居中摆放。 */
    private static double CenterOffset(TerminalGlyph glyph, int span, TerminalFontSet fonts)
        => Math.Max(0, ((span * fonts.Metrics.Width) - glyph.Advance) / 2);

    /** 把同字体同颜色的相邻字形攒成一个 GlyphRun; 中间隔着空格时把空隙并进前一个字形的前进宽度。 */
    private sealed class TextRunBuilder(TerminalFontSet fonts, RowVisual visual)
    {
        private readonly List<GlyphInfo> _glyphs = [];
        private readonly List<char> _characters = [];
        private GlyphTypeface? _typeface;
        private IBrush? _brush;
        private double _start;
        private double _end;

        public void Add(int codepoint, TerminalGlyph glyph, double left, int span, IBrush brush)
        {
            if (!ReferenceEquals(glyph.Typeface, _typeface) || !ReferenceEquals(brush, _brush))
                Flush();
            if (_glyphs.Count == 0)
                (_typeface, _brush, _start, _end) = (glyph.Typeface, brush, left, left);
            if (left > _end)
                WidenLastGlyph(left - _end);
            var advance = span * fonts.Metrics.Width;
            _glyphs.Add(new GlyphInfo(glyph.Index, _characters.Count, advance, new Vector(CenterOffset(glyph, span, fonts), 0)));
            _characters.AddRange(char.ConvertFromUtf32(codepoint));
            _end = left + advance;
        }

        private void WidenLastGlyph(double gap)
        {
            var last = _glyphs[^1];
            _glyphs[^1] = new GlyphInfo(last.GlyphIndex, last.GlyphCluster, last.GlyphAdvance + gap, last.GlyphOffset);
        }

        public void Flush()
        {
            if (_glyphs.Count == 0 || _typeface is null || _brush is null)
                return;
            var run = new GlyphRun(_typeface, fonts.Size, _characters.ToArray(), _glyphs.ToArray(), new Point(_start, fonts.Baseline));
            visual.AddText(run, _brush);
            _glyphs.Clear();
            _characters.Clear();
        }
    }

    /** 一行的排版缓存; Cells 是排版时的行内容, 用于判断下一帧是否需要重排。 */
    private sealed class RowVisual
    {
        private readonly List<(Rect Rect, IBrush Brush)> _backgrounds = [];
        private readonly List<(GlyphRun Run, IBrush Brush)> _texts = [];
        private readonly List<(Rect Rect, IBrush Brush)> _lines = [];

        public Cell[] Cells { get; set; } = [];

        public void AddBackground(Rect rect, IBrush brush) => Merge(_backgrounds, rect, brush);

        public void AddLine(Rect rect, IBrush brush) => Merge(_lines, rect, brush);

        public void AddText(GlyphRun run, IBrush brush) => _texts.Add((run, brush));

        public void Draw(DrawingContext context)
        {
            foreach (var (rect, brush) in _backgrounds)
                context.FillRectangle(brush, rect);
            foreach (var (run, brush) in _texts)
                context.DrawGlyphRun(brush, run);
            foreach (var (rect, brush) in _lines)
                context.FillRectangle(brush, rect);
        }

        public void Clear()
        {
            foreach (var (run, _) in _texts)
                run.Dispose();
            _texts.Clear();
            _backgrounds.Clear();
            _lines.Clear();
            Cells = [];
        }

        /** 同色且首尾相接的矩形合并成一块, 一整行底色只画一次。 */
        private static void Merge(List<(Rect Rect, IBrush Brush)> target, Rect rect, IBrush brush)
        {
            if (target.Count > 0 && ReferenceEquals(target[^1].Brush, brush) && Math.Abs(target[^1].Rect.Right - rect.Left) < 0.01)
            {
                var previous = target[^1].Rect;
                target[^1] = (new Rect(previous.Left, previous.Top, rect.Right - previous.Left, previous.Height), brush);
                return;
            }
            target.Add((rect, brush));
        }
    }
}
