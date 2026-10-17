using Avalonia.Controls;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/** A session in its own window; closing the window closes the session view (the session itself keeps running on its machine). */
public sealed partial class SessionWindow : Window
{
    /** The XAML compiler needs a parameterless constructor; the application uses the one below. */
    public SessionWindow() => InitializeComponent();

    public SessionWindow(FleetSessionViewModel session) : this() => DataContext = session;

    public FleetSessionViewModel? Session => DataContext as FleetSessionViewModel;
}
