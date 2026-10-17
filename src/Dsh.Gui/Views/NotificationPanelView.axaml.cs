using Avalonia.Controls;

namespace Dsh.Gui.Views;

/** The bell's panel: pending approvals and questions of every machine first, then the notification history with filters and read marks. */
public sealed partial class NotificationPanelView : UserControl
{
    public NotificationPanelView() => InitializeComponent();
}
