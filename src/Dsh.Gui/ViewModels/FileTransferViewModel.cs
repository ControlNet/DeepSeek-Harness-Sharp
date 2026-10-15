using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dsh.Gui.ViewModels;

public enum TransferDirection
{
    Upload,
    Download,
}

public enum TransferState
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

/**
 * 文件视图里的一条上传或下载: 字节进度、结果与取消。
 * 进度经构造时捕获的同步上下文回到 UI 线程(Progress&lt;T&gt;), 结束后的迟到进度被忽略, 所以完成时的字节数以传输结果为准。
 */
public sealed partial class FileTransferViewModel : ObservableObject
{
    private const double PercentScale = 100;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly IProgress<long> _progress;

    public FileTransferViewModel(TransferDirection direction, string name, string remotePath, string localPath, long totalBytes)
    {
        (Direction, Name, RemotePath, LocalPath, TotalBytes) = (direction, name, remotePath, localPath, totalBytes);
        _progress = new Progress<long>(bytes =>
        {
            if (State == TransferState.Running)
                TransferredBytes = Math.Max(TransferredBytes, bytes);
        });
    }

    public TransferDirection Direction { get; }

    public string Name { get; }

    public string RemotePath { get; }

    public string LocalPath { get; }

    public long TotalBytes { get; }

    public string DirectionText => Direction == TransferDirection.Upload ? "上传" : "下载";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(ProgressText))]
    private long _transferredBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private TransferState _state;

    [ObservableProperty]
    private string? _error;

    public bool IsRunning => State == TransferState.Running;

    public double Percent => TotalBytes <= 0 ? (State == TransferState.Completed ? PercentScale : 0) : PercentScale * TransferredBytes / TotalBytes;

    public string ProgressText => State switch
    {
        TransferState.Completed => $"完成 {FileSizeText.Format(TransferredBytes)}",
        TransferState.Cancelled => "已取消",
        TransferState.Failed => $"失败: {Error}",
        _ => $"{FileSizeText.Format(TransferredBytes)} / {FileSizeText.Format(TotalBytes)}",
    };

    /** 累计字节数(来自 TransferProgressStream, 任意线程)。 */
    public void Report(long transferred) => _progress.Report(transferred);

    /** 执行传输; work 返回传输的总字节数。异常与取消都记录在 State 里, 不向外抛。 */
    public async Task RunAsync(Func<CancellationToken, Task<long>> work)
    {
        try
        {
            var bytes = await work(_cancellation.Token);
            TransferredBytes = bytes;
            State = TransferState.Completed;
        }
        catch (OperationCanceledException)
        {
            State = TransferState.Cancelled;
        }
        catch (Exception error)
        {
            Error = error.Message;
            State = TransferState.Failed;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cancellation.Cancel();
}
