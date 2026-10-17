using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/** The conversation of a harness or ACP session on a fleet machine: messages kept scrolled to the end and a composer (Enter sends, Shift+Enter breaks the line). */
public sealed partial class SessionTranscriptView : UserControl
{
    private SessionTranscriptViewModel? _viewModel;

    public SessionTranscriptView()
    {
        InitializeComponent();
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.MessagesChanged -= ScrollToEnd;
        _viewModel = DataContext as SessionTranscriptViewModel;
        if (_viewModel is not null)
            _viewModel.MessagesChanged += ScrollToEnd;
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (_viewModel is not { Messages.Count: > 0 } viewModel)
            return;
        var last = viewModel.Messages[^1];
        Dispatcher.UIThread.Post(() => MessageList.ScrollIntoView(last), DispatcherPriority.Background);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || _viewModel is null)
            return;
        e.Handled = true;
        if (_viewModel.SendCommand.CanExecute(null))
            _viewModel.SendCommand.Execute(null);
    }
}
