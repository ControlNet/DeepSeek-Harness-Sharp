using CommunityToolkit.Mvvm.ComponentModel;

namespace Dsh.Gui.ViewModels;

/**
 * 远端操作类视图模型的公共状态: 进行中标记、状态栏文字与内联确认条。
 * 操作一律经 RunAsync 执行, 失败只写进状态栏而不向绑定层抛出(命令绑定里的异常会直接打断 UI 线程)。
 */
public abstract partial class OperationViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _statusIsError;

    /** 等待用户确认的危险操作(删除目录、覆盖远端修改、移除 worktree、删除机器); 同时只有一个。 */
    [ObservableProperty]
    private PendingConfirmation? _confirmation;

    /** 执行一次远端操作: 成功时写 done(为 null 则保留操作自己写的状态), 失败时写异常消息; 返回是否成功。 */
    protected async Task<bool> RunAsync(Func<Task> operation, string? done = null)
    {
        Busy = true;
        try
        {
            await operation();
            if (done is not null)
                Report(done);
            return true;
        }
        catch (OperationCanceledException)
        {
            Report("已取消");
            return false;
        }
        catch (Exception error)
        {
            Report(error.Message, isError: true);
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    protected void Report(string message, bool isError = false)
    {
        Status = message;
        StatusIsError = isError;
    }

    /** 挂起一个需要确认的操作; 确认或取消后确认条消失。 */
    protected void Ask(string message, string acceptLabel, Func<Task> accept)
        => Confirmation = new PendingConfirmation(message, acceptLabel, accept, () => Confirmation = null);
}
