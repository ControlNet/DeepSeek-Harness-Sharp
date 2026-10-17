using Avalonia.Controls;

namespace Dsh.Gui.Views;

/** Machine and path picker over a page that works on one machine's host; DataContext is a HostPageViewModel. */
public sealed partial class HostPageView : UserControl
{
    public HostPageView() => InitializeComponent();
}
