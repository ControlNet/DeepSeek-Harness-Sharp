using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/** 终端控件的输入: 键盘与输入法文本、复制粘贴、鼠标上报或本地选择、滚轮翻看滚回、焦点上报。 */
public sealed partial class TerminalControl
{
    /** 滚轮每一格滚动的行数。 */
    private const int WheelLines = 3;

    private const string FocusInSequence = "\u001b[I";
    private const string FocusOutSequence = "\u001b[O";

    private TerminalImeClient? _imeClient;
    private TerminalSelection? _selection;
    private bool _selecting;
    private int _scrollOffset;
    private double _wheelRemainder;
    private TerminalMouseButton? _reportedButton;
    private (int Column, int Row) _lastMouseCell = (-1, -1);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _attached is not { } session)
            return;
        if (TryShortcut(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        bool applicationCursorKeys;
        lock (session.Gate)
            applicationCursorKeys = session.Screen.ApplicationCursorKeys;
        var bytes = TerminalInputEncoder.EncodeKey(e.Key, e.KeyModifiers, e.KeySymbol, applicationCursorKeys);
        if (bytes.Length == 0)
            return;
        SendInput(bytes);
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || _attached is null || string.IsNullOrEmpty(e.Text))
            return;
        var bytes = TerminalInputEncoder.EncodeText(e.Text);
        if (bytes.Length > 0)
            SendInput(bytes);
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        ReportFocus(FocusInSequence);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        ReportFocus(FocusOutSequence);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_attached is not { } session)
            return;
        var point = e.GetCurrentPoint(this);
        var cell = CellAt(point.Position);
        _lastMouseCell = cell;
        if (ReportsMouse(session, e.KeyModifiers))
            _reportedButton = ReportButton(session, point.Properties.PointerUpdateKind, TerminalMouseAction.Press, cell, e.KeyModifiers);
        else if (point.Properties.IsRightButtonPressed)
            _ = CopyOrPasteAsync();
        else if (point.Properties.IsLeftButtonPressed)
            BeginSelection(cell);
        else
            return;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_attached is not { } session)
            return;
        var cell = CellAt(e.GetPosition(this));
        if (_selecting && _selection is { } selection)
        {
            _selection = selection with { Active = new TerminalCellPosition(cell.Row - _scrollOffset, cell.Column) };
            InvalidateVisual();
        }
        else if (cell != _lastMouseCell && ReportsMouse(session, e.KeyModifiers))
            ReportMotion(session, cell, e.KeyModifiers);
        _lastMouseCell = cell;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (_selecting)
        {
            _selecting = false;
            if (_selection is { IsEmpty: true })
                _selection = null;
            InvalidateVisual();
        }
        else if (_reportedButton is { } button && _attached is { } session)
        {
            Report(session, new TerminalMouseEvent(TerminalMouseAction.Release, button, _lastMouseCell.Column, _lastMouseCell.Row, ToConsoleModifiers(e.KeyModifiers)));
            _reportedButton = null;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_attached is not { } session)
            return;
        _wheelRemainder += e.Delta.Y;
        var notches = (int)_wheelRemainder;
        _wheelRemainder -= notches;
        if (notches != 0)
            Wheel(session, notches, CellAt(e.GetPosition(this)), e.KeyModifiers);
        e.Handled = true;
    }

    /** 滚轮: 子进程要鼠标时发滚轮事件; 备用屏(全屏程序)里换成方向键; 主屏翻看滚回。 */
    private void Wheel(TerminalSession session, int notches, (int Column, int Row) cell, KeyModifiers modifiers)
    {
        if (ReportsMouse(session, modifiers))
        {
            var button = notches > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
            for (var index = 0; index < Math.Abs(notches); index++)
                Report(session, new TerminalMouseEvent(TerminalMouseAction.Press, button, cell.Column, cell.Row, ToConsoleModifiers(modifiers)));
            return;
        }
        bool alternate, applicationCursorKeys;
        lock (session.Gate)
            (alternate, applicationCursorKeys) = (session.Screen.IsAlternateScreen, session.Screen.ApplicationCursorKeys);
        if (!alternate)
        {
            ScrollBy(session, notches * WheelLines);
            return;
        }
        var arrow = TerminalInputEncoder.EncodeKey(notches > 0 ? Key.Up : Key.Down, KeyModifiers.None, null, applicationCursorKeys);
        for (var index = 0; index < Math.Abs(notches) * WheelLines; index++)
            session.Send(arrow);
    }

    /** 终端自己的快捷键: Ctrl+Shift+C 复制, Ctrl+Shift+V / Shift+Insert 粘贴, Shift+PgUp/PgDn 翻看滚回。 */
    private bool TryShortcut(Key key, KeyModifiers modifiers)
    {
        var controlShift = modifiers == (KeyModifiers.Control | KeyModifiers.Shift);
        if (controlShift && key == Key.C)
            _ = CopyAsync();
        else if ((controlShift && key == Key.V) || (modifiers == KeyModifiers.Shift && key == Key.Insert))
            _ = PasteAsync();
        else if (modifiers == KeyModifiers.Shift && key is Key.PageUp or Key.PageDown && _attached is { } session)
            ScrollBy(session, (key == Key.PageUp ? 1 : -1) * Math.Max(1, GridSize.Rows - 1));
        else
            return false;
        return true;
    }

    private void SendInput(ReadOnlySpan<byte> bytes)
    {
        _scrollOffset = 0;
        _selection = null;
        _attached?.Send(bytes);
        InvalidateVisual();
    }

    private void ReportFocus(string sequence)
    {
        InvalidateVisual();
        if (_attached is not { } session)
            return;
        bool reporting;
        lock (session.Gate)
            reporting = session.Screen.FocusReporting;
        if (reporting)
            session.Send(System.Text.Encoding.ASCII.GetBytes(sequence));
    }

    private void ScrollBy(TerminalSession session, int lines)
    {
        lock (session.Gate)
            _scrollOffset = ClampScroll(session.Screen, _scrollOffset + lines);
        InvalidateVisual();
    }

    /** 备用屏没有滚回可看; 主屏最多翻到最旧的一行滚回。调用方需持有屏幕锁。 */
    private static int ClampScroll(VtScreen screen, int offset)
        => screen.IsAlternateScreen ? 0 : Math.Clamp(offset, 0, screen.ScrollbackCount);

    private void BeginSelection((int Column, int Row) cell)
    {
        var position = new TerminalCellPosition(cell.Row - _scrollOffset, cell.Column);
        _selection = new TerminalSelection(position, position);
        _selecting = true;
        InvalidateVisual();
    }

    private (int Column, int Row) CellAt(Point point) => Fonts().Metrics.CellAt(point, Padding, GridSize);

    /** 子进程申请了鼠标上报时把鼠标交给它; 按住 Shift 则保留给本地选择(xterm 惯例)。 */
    private static bool ReportsMouse(TerminalSession session, KeyModifiers modifiers)
    {
        if ((modifiers & KeyModifiers.Shift) != 0)
            return false;
        lock (session.Gate)
            return session.Screen.MouseTracking != TerminalMouseTracking.None;
    }

    private TerminalMouseButton? ReportButton(TerminalSession session, PointerUpdateKind kind, TerminalMouseAction action, (int Column, int Row) cell, KeyModifiers modifiers)
    {
        var button = kind switch
        {
            PointerUpdateKind.LeftButtonPressed => TerminalMouseButton.Left,
            PointerUpdateKind.MiddleButtonPressed => TerminalMouseButton.Middle,
            PointerUpdateKind.RightButtonPressed => TerminalMouseButton.Right,
            _ => (TerminalMouseButton?)null,
        };
        if (button is { } pressed)
            Report(session, new TerminalMouseEvent(action, pressed, cell.Column, cell.Row, ToConsoleModifiers(modifiers)));
        return button;
    }

    private void ReportMotion(TerminalSession session, (int Column, int Row) cell, KeyModifiers modifiers)
    {
        var button = _reportedButton ?? TerminalMouseButton.None;
        Report(session, new TerminalMouseEvent(TerminalMouseAction.Motion, button, cell.Column, cell.Row, ToConsoleModifiers(modifiers)));
    }

    /** 按子进程申请的上报级别过滤后发送; 过滤与编码都在 TerminalMouseEncoder 里。 */
    private static void Report(TerminalSession session, TerminalMouseEvent mouse)
    {
        byte[]? bytes = null;
        lock (session.Gate)
        {
            var screen = session.Screen;
            if (TerminalMouseEncoder.Reports(screen.MouseTracking, mouse.Action, mouse.Button != TerminalMouseButton.None))
                bytes = TerminalMouseEncoder.Encode(mouse, screen.SgrMouse);
        }
        if (bytes is not null)
            session.Send(bytes);
    }

    private static ConsoleModifiers ToConsoleModifiers(KeyModifiers modifiers)
        => ((modifiers & KeyModifiers.Shift) != 0 ? ConsoleModifiers.Shift : 0)
            | ((modifiers & KeyModifiers.Alt) != 0 ? ConsoleModifiers.Alt : 0)
            | ((modifiers & KeyModifiers.Control) != 0 ? ConsoleModifiers.Control : 0);

    /** 右键: 有选区时复制, 否则粘贴。 */
    private async Task CopyOrPasteAsync()
    {
        if (_selection is { IsEmpty: false })
            await CopyAsync();
        else
            await PasteAsync();
    }

    private async Task CopyAsync()
    {
        if (_attached is not { } session || _selection is not { IsEmpty: false } selection || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        string text;
        lock (session.Gate)
            text = selection.ExtractText(session.Screen);
        await clipboard.SetTextAsync(text);
    }

    private async Task PasteAsync()
    {
        if (_attached is not { } session || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;
        bool bracketed;
        lock (session.Gate)
            bracketed = session.Screen.BracketedPaste;
        SendInput(TerminalPasteEncoder.Encode(text, bracketed));
    }

    /** 最小输入法客户端: 不做预编辑与上下文文本, 只提供光标位置让候选框跟随, 提交的文本经 TextInput 到达。 */
    private sealed class TerminalImeClient(TerminalControl owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;

        public override bool SupportsPreedit => false;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => "";

        public override Rect CursorRectangle => owner.CursorRectangle();

        public override TextSelection Selection { get; set; }
    }
}
