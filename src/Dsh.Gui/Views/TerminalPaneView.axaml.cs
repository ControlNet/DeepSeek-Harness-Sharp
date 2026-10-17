using Avalonia.Controls;
using Dsh.Gui.ViewModels;
using Dsh.Pty;

namespace Dsh.Gui.Views;

/** A terminal inside a session view tab: the terminal control plus its title and status line, reporting title and grid changes to the view model. */
public sealed partial class TerminalPaneView : UserControl
{
    public TerminalPaneView()
    {
        InitializeComponent();
        Terminal.TitleChanged += OnTitleChanged;
        Terminal.GridResized += OnGridResized;
    }

    private void OnTitleChanged(object? sender, string title)
    {
        if (DataContext is TerminalViewModel terminal)
            terminal.ScreenTitle = title;
    }

    private void OnGridResized(object? sender, TerminalSize size)
    {
        if (DataContext is TerminalViewModel terminal)
            terminal.OnGridResized(size);
    }
}
