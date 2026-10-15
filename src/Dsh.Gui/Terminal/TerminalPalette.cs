using Avalonia.Media;
using Avalonia.Media.Immutable;
using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/** 终端配色: 默认前景/背景、16 色板(Campbell)、光标与选区; 并把单元格的颜色与样式解析成实际绘制色。 */
public static class TerminalPalette
{
    public static readonly Color DefaultForeground = Color.FromRgb(0xCC, 0xCC, 0xCC);
    public static readonly Color DefaultBackground = Color.FromRgb(0x1E, 0x1E, 0x1E);
    public static readonly Color CursorColor = Color.FromRgb(0xE6, 0xE6, 0xE6);
    public static readonly Color SelectionColor = Color.FromArgb(0x70, 0x26, 0x6F, 0xC8);

    /** Dim(SGR 2)的前景色向背景色混合的比例。 */
    private const double DimBlend = 0.5;

    /** AnsiColor 1..16 对应的颜色; 下标 0 是 AnsiColor.Default, 不会被取用。 */
    private static readonly Color[] Ansi =
    [
        DefaultForeground,
        Color.FromRgb(0x0C, 0x0C, 0x0C), Color.FromRgb(0xC5, 0x0F, 0x1F), Color.FromRgb(0x13, 0xA1, 0x0E), Color.FromRgb(0xC1, 0x9C, 0x00),
        Color.FromRgb(0x00, 0x37, 0xDA), Color.FromRgb(0x88, 0x17, 0x98), Color.FromRgb(0x3A, 0x96, 0xDD), Color.FromRgb(0xCC, 0xCC, 0xCC),
        Color.FromRgb(0x76, 0x76, 0x76), Color.FromRgb(0xE7, 0x48, 0x56), Color.FromRgb(0x16, 0xC6, 0x0C), Color.FromRgb(0xF9, 0xF1, 0xA5),
        Color.FromRgb(0x3B, 0x78, 0xFF), Color.FromRgb(0xB4, 0x00, 0x9E), Color.FromRgb(0x61, 0xD6, 0xD6), Color.FromRgb(0xF2, 0xF2, 0xF2),
    ];

    private static readonly Dictionary<Color, IImmutableSolidColorBrush> Brushes = [];

    public static CellColor ToCellColor(Color color) => CellColor.FromRgb(color.R, color.G, color.B);

    /** 单元格最终的前景/背景: 反显交换前后景, Dim 把前景向背景混合。 */
    public static (Color Foreground, Color Background) ColorsOf(Cell cell)
    {
        var foreground = Resolve(cell.Foreground, DefaultForeground);
        var background = Resolve(cell.Background, DefaultBackground);
        if ((cell.Style & CellStyle.Reverse) != 0)
            (foreground, background) = (background, foreground);
        if ((cell.Style & CellStyle.Dim) != 0)
            foreground = Blend(foreground, background, DimBlend);
        return (foreground, background);
    }

    /** 画刷只在 UI 线程上取用, 按颜色缓存不可变画刷, 避免每帧为每个片段分配。 */
    public static IImmutableSolidColorBrush BrushOf(Color color)
    {
        if (!Brushes.TryGetValue(color, out var brush))
        {
            brush = new ImmutableSolidColorBrush(color);
            Brushes[color] = brush;
        }
        return brush;
    }

    private static Color Resolve(CellColor color, Color fallback) => color.Kind switch
    {
        CellColorKind.Rgb => Color.FromRgb(color.R, color.G, color.B),
        CellColorKind.Palette when color.Palette < Ansi.Length => Ansi[color.Palette],
        _ => fallback,
    };

    private static Color Blend(Color from, Color to, double amount)
        => Color.FromRgb(Mix(from.R, to.R, amount), Mix(from.G, to.G, amount), Mix(from.B, to.B, amount));

    private static byte Mix(byte from, byte to, double amount) => (byte)Math.Round(from + ((to - from) * amount));
}
