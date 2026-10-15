using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Gui.Terminal;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/** GUI 终端窗口: 本地 PTY 与远端 PTY 都包成 TerminalSession 交给终端控件, 只换底层连接(§14 C)。 */
public sealed partial class TerminalViewModel : ObservableObject, IAsyncDisposable
{
    /** 启动时的 PTY 尺寸; 控件完成布局后立即按实际格子数 resize。 */
    private static readonly TerminalSize InitialSize = new(80, 24);

    /** 本地终端声明为 xterm 兼容真彩终端; GUI 进程从桌面启动时自身可能没有 TERM。 */
    private static readonly IReadOnlyDictionary<string, string?> LocalEnvironment = new Dictionary<string, string?>
    {
        ["TERM"] = "xterm-256color",
        ["COLORTERM"] = "truecolor",
    };

    private PtySession? _local;
    private RemotePtyConnection? _remote;
    private string _origin = "";

    public TerminalViewModel(string title) => Title = title;

    public string Title { get; }

    [ObservableProperty]
    private TerminalSession? _session;

    [ObservableProperty]
    private string _status = "正在启动…";

    /** 子进程经 OSC 设置的标题(shell 提示符、vim 文件名等)。 */
    [ObservableProperty]
    private string _screenTitle = "";

    /** 本地终端: 进程内 PTY。 */
    public async Task StartLocalAsync(string homeRoot)
    {
        var shell = PtyShell.Resolve();
        _local = await PtyHost.Default.StartAsync(new PtyStartInfo
        {
            FileName = shell,
            Arguments = [.. PtyShell.Arguments(shell)],
            Home = homeRoot,
            Rows = InitialSize.Rows,
            Columns = InitialSize.Columns,
            Environment = LocalEnvironment,
        });
        Attach(_local, $"本地 · {shell}");
    }

    /** 远端终端: 经 RPC 起 PTY, 本地只搬运字节。 */
    public async Task StartRemoteAsync(IRemoteHost host, string platform)
    {
        var (fileName, arguments) = platform == "windows"
            ? ("cmd.exe", (string[])[])
            : ("/bin/sh", (string[])["-l"]);
        _remote = await RemotePtyConnection.StartAsync(host, fileName, arguments, CancellationToken.None);
        Attach(_remote, $"远端 · {fileName}");
    }

    /** 控件按自身尺寸算出新的格子数后回调, 用于状态栏显示。 */
    public void OnGridResized(TerminalSize size) => Status = $"{_origin} · {size.Columns}x{size.Rows}";

    public async ValueTask DisposeAsync()
    {
        if (Session is not null)
            await Session.DisposeAsync();
        if (_local is not null)
        {
            await PtyHost.Default.StopAsync(_local.Id.Value);
            _local.Dispose();
        }
        if (_remote is not null)
            await _remote.DisposeAsync();
    }

    private void Attach(ITerminalConnection connection, string origin)
    {
        _origin = origin;
        var session = new TerminalSession(connection, InitialSize);
        session.Exited += () => Dispatcher.UIThread.Post(() => Status = $"{_origin} · 已退出");
        session.Start();
        Status = origin;
        Session = session;
    }
}
