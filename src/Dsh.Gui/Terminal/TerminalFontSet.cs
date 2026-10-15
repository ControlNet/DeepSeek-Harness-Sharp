using System.Globalization;
using Avalonia.Media;
using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/** 解析出的字形: 所在字体与字形号, 以及字形自身的前进宽度(像素)。 */
public readonly record struct TerminalGlyph(GlyphTypeface Typeface, ushort Index, double Advance);

/**
 * 终端字体: 由等宽主字体给出格子尺寸与基线, 四种样式(常规/粗/斜/粗斜)各取一个字形表。
 * 主字体缺字(中文、符号)时向系统回退字体逐字查找, 结果按码点与样式缓存。
 */
public sealed class TerminalFontSet
{
    private const char MeasureCharacter = 'M';

    private readonly FontFamily _family;
    private readonly GlyphTypeface[] _faces;
    private readonly Dictionary<(int Codepoint, int Face), TerminalGlyph> _cache = [];

    public TerminalFontSet(FontFamily family, double size, double renderScaling)
    {
        _family = family;
        Size = size;
        _faces = [Load(family, FontStyle.Normal, FontWeight.Normal), Load(family, FontStyle.Normal, FontWeight.Bold), Load(family, FontStyle.Italic, FontWeight.Normal), Load(family, FontStyle.Italic, FontWeight.Bold)];
        var primary = _faces[0];
        var scale = size / primary.Metrics.DesignEmHeight;
        var advance = Resolve(MeasureCharacter, CellStyle.None).Advance;
        Metrics = new TerminalCellMetrics(Snap(advance, renderScaling), Snap(primary.Metrics.LineSpacing * scale, renderScaling));
        var ascent = Math.Abs(primary.Metrics.Ascent) * scale;
        Baseline = Math.Round((ascent + ((Metrics.Height - (primary.Metrics.LineSpacing * scale)) / 2)) * renderScaling) / renderScaling;
        UnderlineOffset = Math.Max(1, Math.Abs(primary.Metrics.UnderlinePosition) * scale);
        LineThickness = Math.Max(1, primary.Metrics.UnderlineThickness * scale);
    }

    public double Size { get; }

    public TerminalCellMetrics Metrics { get; }

    /** 基线距格子顶部的距离。 */
    public double Baseline { get; }

    /** 下划线在基线下方的距离。 */
    public double UnderlineOffset { get; }

    public double LineThickness { get; }

    public bool IsFixedPitch => _faces[0].Metrics.IsFixedPitch;

    public string PrimaryFamilyName => _faces[0].FamilyName;

    public TerminalGlyph Resolve(int codepoint, CellStyle style)
    {
        var face = FaceIndex(style);
        if (_cache.TryGetValue((codepoint, face), out var glyph))
            return glyph;
        glyph = Lookup(codepoint, face);
        _cache[(codepoint, face)] = glyph;
        return glyph;
    }

    private TerminalGlyph Lookup(int codepoint, int face)
    {
        var typeface = _faces[face];
        if (typeface.CharacterToGlyphMap.TryGetGlyph(codepoint, out var index) && index != 0)
            return Measure(typeface, index);
        var style = face >= 2 ? FontStyle.Italic : FontStyle.Normal;
        var weight = face % 2 == 1 ? FontWeight.Bold : FontWeight.Normal;
        if (FontManager.Current.TryMatchCharacter(codepoint, style, weight, FontStretch.Normal, _family, CultureInfo.CurrentUICulture, out var fallback)
            && fallback.GlyphTypeface.CharacterToGlyphMap.TryGetGlyph(codepoint, out var fallbackIndex))
            return Measure(fallback.GlyphTypeface, fallbackIndex);
        return Measure(typeface, index);
    }

    private TerminalGlyph Measure(GlyphTypeface typeface, ushort index)
    {
        var advance = typeface.TryGetHorizontalGlyphAdvance(index, out var designAdvance) ? designAdvance : typeface.Metrics.DesignEmHeight / 2;
        return new TerminalGlyph(typeface, index, advance * Size / typeface.Metrics.DesignEmHeight);
    }

    private static int FaceIndex(CellStyle style)
        => ((style & CellStyle.Italic) != 0 ? 2 : 0) + ((style & CellStyle.Bold) != 0 ? 1 : 0);

    private static GlyphTypeface Load(FontFamily family, FontStyle style, FontWeight weight)
    {
        if (FontManager.Current.TryGetGlyphTypeface(new Typeface(family, style, weight), out var face))
            return face;
        return new Typeface(FontFamily.Default, style, weight).GlyphTypeface;
    }

    /** 格子尺寸对齐到设备像素, 否则逐格累加的小数误差会让背景色块之间出现细缝。 */
    private static double Snap(double value, double renderScaling) => Math.Ceiling(value * renderScaling) / renderScaling;
}
