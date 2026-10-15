using Avalonia.Controls;

namespace Dsh.Gui.Views;

/** One session on a fleet machine: header with status and pending requests, then the session tab and extra shell tabs. */
public sealed partial class FleetSessionView : UserControl
{
    public FleetSessionView() => InitializeComponent();
}
