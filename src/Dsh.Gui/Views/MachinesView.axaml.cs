using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/**
 * 机器管理页; DataContext 为 MachinesViewModel(由集成方构造并调用 Load, 有常驻 FleetClient 时调用 AttachFleet, 订阅 MachinesChanged 重建连接)。
 * 诊断结果的修复命令可以选中复制, 也可以一键复制到剪贴板。
 */
public sealed partial class MachinesView : UserControl
{
    public MachinesView() => InitializeComponent();

    private async void OnCopyFixClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DoctorCheckRow { Fix: { } fix } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(fix);
    }
}
