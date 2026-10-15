using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/**
 * 自绘 VT 终端控件: 显示 TerminalSession 的屏幕, 把键盘/粘贴/鼠标编码后送回会话, 尺寸变化时按格子数通知会话 resize。
 * 会话输出在后台线程到达, 控件只置一个待绘标记并把同一帧内的多次输出合并成一次重绘。
 */
public sealed partial class TerminalControl : Control
{
    public static readonly StyledProperty<TerminalSession?> SessionProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalSession?>(nameof(Session));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, FontFamily>(nameof(FontFamily), FontFamily.Parse(DefaultFontFamily));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(FontSize), DefaultFontSize);

    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<TerminalControl, Thickness>(nameof(Padding), new Thickness(DefaultPadding));

    private const string DefaultFontFamily = "Cascadia Mono, JetBrains Mono, Consolas, Menlo, DejaVu Sans Mono, Noto Sans Mono, Liberation Mono, monospace";
    private const double DefaultFontSize = 13;
    private const double DefaultPadding = 6;
    private const double CursorBarWidth = 2;

    /** 失焦时空心光标框的线宽。 */
    private const double CursorOutlineWidth = 1;

    /** 布局给出无限空间(如放进 ScrollViewer)时按这个格子数申请尺寸。 */
    private static readonly TerminalSize DefaultGrid = new(80, 24);

    private static readonly ImmutablePen CursorOutline = new(TerminalPalette.BrushOf(TerminalPalette.CursorColor), CursorOutlineWidth);

    private readonly TerminalRowRenderer _rows = new();
    private readonly TerminalFrame _frame = new();
    private TerminalFontSet? _fonts;
    private TerminalSession? _attached;
    private GlyphRun? _cursorGlyph;
    private string _title = "";
    private int _framePending;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        TextInputMethodClientRequested += (_, e) => e.Client = _imeClient ??= new TerminalImeClient(this);
    }

    public TerminalSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /** 当前按控件尺寸算出的列数与行数。 */
    public TerminalSize GridSize { get; private set; } = DefaultGrid;

    /** 格子数变化(已同步给会话)后触发, 供调用方显示尺寸或转发给其它通道。 */
    public event EventHandler<TerminalSize>? GridResized;

    /** 子进程经 OSC 0/2 改了标题。 */
    public event EventHandler<string>? TitleChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty)
            Rebind();
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty || change.Property == PaddingProperty)
            ResetFonts();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ResetFonts();
        Rebind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unbind();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var preferred = Fonts().Metrics.SizeFor(DefaultGrid, Padding);
        return new Size(
            double.IsInfinity(availableSize.Width) ? preferred.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? preferred.Height : availableSize.Height);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateGrid();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(TerminalPalette.BrushOf(TerminalPalette.DefaultBackground), new Rect(Bounds.Size));
        if (_attached is not { } session)
            return;
        var fonts = Fonts();
        lock (session.Gate)
        {
            _scrollOffset = ClampScroll(session.Screen, _scrollOffset);
            _frame.Capture(session.Screen, _scrollOffset);
        }
        _rows.Draw(context, _frame, Padding);
        DrawSelection(context, fonts);
        DrawCursor(context, fonts);
    }

    private TerminalFontSet Fonts()
    {
        if (_fonts is not null)
            return _fonts;
        _fonts = new TerminalFontSet(FontFamily, FontSize, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        _rows.Reset(_fonts);
        return _fonts;
    }

    private void ResetFonts()
    {
        _fonts = null;
        InvalidateMeasure();
        UpdateGrid();
        InvalidateVisual();
    }

    /** 按当前尺寸重算格子数; 变化时先改会话(屏幕模型 + 对端 PTY)再通知调用方。 */
    private void UpdateGrid()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var grid = Fonts().Metrics.GridFor(Bounds.Size, Padding);
        var changed = grid != GridSize;
        GridSize = grid;
        // 会话的屏幕已是该尺寸时 Resize 什么也不做; 新绑定的会话靠这里拿到控件的实际尺寸。
        _attached?.Resize(grid);
        if (changed)
            GridResized?.Invoke(this, grid);
    }

    private void Rebind()
    {
        Unbind();
        if (Session is not { } session || VisualRoot is null)
            return;
        _attached = session;
        session.Changed += OnSessionChanged;
        UpdateGrid();
        InvalidateVisual();
    }

    private void Unbind()
    {
        if (_attached is null)
            return;
        _attached.Changed -= OnSessionChanged;
        _attached = null;
        _selection = null;
        _scrollOffset = 0;
    }

    /** 后台线程上的输出通知: 只有"空闲 → 待绘"的跳变才投递一次 UI 回调, 一帧内的大量小块输出合并成一次重绘。 */
    private void OnSessionChanged()
    {
        if (Interlocked.Exchange(ref _framePending, 1) == 0)
            Dispatcher.UIThread.Post(OnFrame);
    }

    private void OnFrame()
    {
        Interlocked.Exchange(ref _framePending, 0);
        if (_attached is not { } session)
            return;
        string title;
        lock (session.Gate)
            title = session.Screen.Title;
        if (title != _title)
        {
            _title = title;
            TitleChanged?.Invoke(this, title);
        }
        InvalidateVisual();
    }

    private void DrawSelection(DrawingContext context, TerminalFontSet fonts)
    {
        if (_selection is not { IsEmpty: false } selection)
            return;
        var brush = TerminalPalette.BrushOf(TerminalPalette.SelectionColor);
        for (var y = 0; y < _frame.Rows; y++)
        {
            if (selection.ColumnsOn(y - _frame.ScrollOffset, _frame.Columns) is (int first, int last))
                context.FillRectangle(brush, fonts.Metrics.CellRect(first, y, Padding, last - first + 1));
        }
    }

    private void DrawCursor(DrawingContext context, TerminalFontSet fonts)
    {
        if (!_frame.CursorVisible || _frame.CursorRow >= _frame.Rows || _frame.CursorColumn >= _frame.Columns)
            return;
        var cell = _frame.Row(_frame.CursorRow)[_frame.CursorColumn];
        var rect = fonts.Metrics.CellRect(_frame.CursorColumn, _frame.CursorRow, Padding, TerminalTextWidth.Of(cell.Character));
        var brush = TerminalPalette.BrushOf(TerminalPalette.CursorColor);
        if (!IsFocused)
        {
            context.DrawRectangle(null, CursorOutline, rect.Deflate(CursorOutlineWidth / 2));
            return;
        }
        if (_frame.CursorShape == TerminalCursorShape.Bar)
            context.FillRectangle(brush, rect.WithWidth(CursorBarWidth));
        else if (_frame.CursorShape == TerminalCursorShape.Underline)
            context.FillRectangle(brush, new Rect(rect.X, rect.Bottom - CursorBarWidth, rect.Width, CursorBarWidth));
        else
            DrawBlockCursor(context, cell, rect, brush);
    }

    /** 方块光标: 先铺光标色, 再用格子背景色重画该格字形, 保证光标下的字仍可读。 */
    private void DrawBlockCursor(DrawingContext context, Cell cell, Rect rect, IBrush brush)
    {
        context.FillRectangle(brush, rect);
        _cursorGlyph?.Dispose();
        _cursorGlyph = _rows.BuildCellGlyph(cell, rect.TopLeft);
        if (_cursorGlyph is not null)
            context.DrawGlyphRun(TerminalPalette.BrushOf(TerminalPalette.ColorsOf(cell).Background), _cursorGlyph);
    }

    /** 输入法候选框锚定的位置: 光标所在格。 */
    private Rect CursorRectangle()
        => _fonts is { } fonts ? fonts.Metrics.CellRect(_frame.CursorColumn, _frame.CursorRow, Padding) : default;
}
