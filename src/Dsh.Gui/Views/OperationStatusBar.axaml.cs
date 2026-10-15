using Avalonia.Controls;

namespace Dsh.Gui.Views;

/** 远端操作类视图共用的底栏: 进行中指示、状态文字(失败标红)与危险操作的内联确认条; DataContext 为任一 OperationViewModel。 */
public sealed partial class OperationStatusBar : UserControl
{
    public OperationStatusBar() => InitializeComponent();
}
