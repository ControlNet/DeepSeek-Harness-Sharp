using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/** Quick switcher overlay (Ctrl+K): Up/Down move, Enter jumps to the session, Escape or a click outside closes. */
public sealed partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        QueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
                Dispatcher.UIThread.Post(() => QueryBox.Focus(), DispatcherPriority.Input);
        };
    }

    private CommandPaletteViewModel? ViewModel => DataContext as CommandPaletteViewModel;

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } palette)
            return;
        e.Handled = e.Key is Key.Up or Key.Down or Key.Enter or Key.Escape;
        if (e.Key == Key.Up)
            palette.MoveCommand.Execute(-1);
        else if (e.Key == Key.Down)
            palette.MoveCommand.Execute(1);
        else if (e.Key == Key.Enter)
            palette.ConfirmCommand.Execute(null);
        else if (e.Key == Key.Escape)
            palette.CloseCommand.Execute(null);
        if (palette.Selected is { } selected)
            ResultList.ScrollIntoView(selected);
    }

    private void OnResultTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionCardViewModel card })
            ViewModel?.ConfirmCommand.Execute(card);
    }

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e) => ViewModel?.CloseCommand.Execute(null);

    private void OnPanelPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
