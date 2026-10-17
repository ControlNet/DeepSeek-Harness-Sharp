using CommunityToolkit.Mvvm.Input;

namespace Dsh.Gui.ViewModels;

/** 视图内联确认条的状态: 一句说明 + 确认按钮文字; 确认才执行操作, 两个按钮都会先让确认条消失。 */
public sealed partial class PendingConfirmation(string message, string acceptLabel, Func<Task> accept, Action close)
{
    public string Message => message;

    public string AcceptLabel => acceptLabel;

    [RelayCommand]
    private async Task AcceptAsync()
    {
        close();
        await accept();
    }

    [RelayCommand]
    private void Cancel() => close();
}
