using Avalonia.Controls;

namespace Dsh.Gui.Views;

/** The start page: every machine's sessions grouped by machine and project, with summary, filters, search and sort. */
public sealed partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
}
