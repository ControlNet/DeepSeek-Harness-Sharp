using Avalonia.Controls;

namespace Dsh.Gui.Views;

/**
 * 只读 Git 视图; DataContext 为 GitViewModel(由集成方构造并调用 RefreshAsync, 订阅其 NewSessionRequested)。
 * diff 用 DiffView 渲染 GitViewModel.DiffCards(每个文件一张卡片)。
 */
public sealed partial class GitView : UserControl
{
    public GitView() => InitializeComponent();
}
