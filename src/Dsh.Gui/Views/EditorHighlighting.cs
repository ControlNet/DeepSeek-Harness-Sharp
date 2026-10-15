using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace Dsh.Gui.Views;

/**
 * 文件编辑器的语法高亮: 按扩展名取 AvaloniaEdit 内置的 xshd 定义(C#、JS、Python、XML、JSON、CSS、C++、Markdown 等)。
 * 内置配色是为浅色背景设计的, 深色主题下把前景色的亮度抬到 MinDarkLightness 以上; 原始颜色记下来, 切回浅色主题时还原。
 * 定义对象是全局共享的, 所以所有编辑器跟随当前应用主题; 只在 UI 线程调用。
 */
internal static class EditorHighlighting
{
    private const double MinDarkLightness = 0.72;

    private static readonly Dictionary<HighlightingColor, HighlightingBrush?> Originals = [];

    /** 没有对应定义(纯文本、未知扩展名)时返回 null。 */
    public static IHighlightingDefinition? For(string extension, bool dark)
    {
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(extension);
        if (definition is null)
            return null;
        foreach (var color in definition.NamedHighlightingColors)
            Apply(color, dark);
        return definition;
    }

    private static void Apply(HighlightingColor color, bool dark)
    {
        if (!Originals.TryGetValue(color, out var original))
            Originals[color] = original = color.Foreground;
        color.Foreground = dark && original?.GetColor(null) is { } light ? new SimpleHighlightingBrush(Lighten(light)) : original;
    }

    private static Color Lighten(Color color)
    {
        var hsl = color.ToHsl();
        return new HslColor(hsl.A, hsl.H, hsl.S, Math.Max(hsl.L, MinDarkLightness)).ToRgb();
    }
}
