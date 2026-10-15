using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Dsh.Gui.Terminal;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;

namespace Dsh.Tests;

/** 终端控件组件测试: headless Avalonia + Skia 真实渲染, 接本机真实 PTY(/bin/sh), 验证键盘与输入法文本、resize 与绘制。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class TerminalControlHeadlessTests
{
    private static readonly TimeSpan OutputTimeout = TimeSpan.FromSeconds(20);
    private static readonly string ScreenshotDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts020", "gui-screenshots"));

    [Fact]
    public async Task Typed_Text_Reaches_The_Pty_And_Resize_Reaches_The_Child() => await HeadlessGui.RunAsync(async () =>
    {
        if (OperatingSystem.IsWindows())
            return;   // 用 /bin/sh 与 stty 验证, 与其它 PTY 用例一致
        using var host = new PtyHost();
        var pty = await host.StartAsync(new PtyStartInfo { FileName = "/bin/sh", Rows = 24, Columns = 80 });
        await using var session = new TerminalSession(pty, new TerminalSize(80, 24));
        session.Start();
        var terminal = new TerminalControl { Session = session };
        var window = new Window { Width = 700, Height = 400, Content = terminal };
        window.Show();
        terminal.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("echo 中文-$((40+2))");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await WaitForLineAsync(session, line => line.StartsWith("中文-42", StringComparison.Ordinal));

        window.Width = 900;
        window.Height = 500;
        Dispatcher.UIThread.RunJobs();
        var grid = terminal.GridSize;
        Assert.True(grid.Columns > 80, $"grid {grid}");
        window.KeyTextInput("stty size");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await WaitForLineAsync(session, line => line == $"{grid.Rows} {grid.Columns}");

        Capture(window, "terminal-headless-sh");
        await pty.StopAsync();
    });

    [Fact]
    public async Task Ctrl_C_Interrupts_The_Foreground_Program() => await HeadlessGui.RunAsync(async () =>
    {
        if (OperatingSystem.IsWindows())
            return;
        using var host = new PtyHost();
        var pty = await host.StartAsync(new PtyStartInfo { FileName = "/bin/sh", Rows = 24, Columns = 80 });
        await using var session = new TerminalSession(pty, new TerminalSize(80, 24));
        session.Start();
        var terminal = new TerminalControl { Session = session };
        var window = new Window { Width = 700, Height = 400, Content = terminal };
        window.Show();
        terminal.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("sleep 30; echo slept");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        window.KeyTextInput("echo after-$((6*7))");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        await WaitForLineAsync(session, line => line == "after-42");
        await pty.StopAsync();
    });

    [Fact]
    public async Task Terminal_Window_Shows_The_Grid_And_Dispose_Stops_The_Local_Shell() => await HeadlessGui.RunAsync(async () =>
    {
        if (OperatingSystem.IsWindows())
            return;
        // PtyHost.Default 是进程级单例: 用唯一的 Home 认出本用例起的会话, 不受其它用例的会话干扰。
        var home = Path.Combine(AppContext.BaseDirectory, $"terminal-window-home-{Guid.NewGuid():N}");
        var viewModel = new TerminalViewModel("本地终端");
        var window = new TerminalWindow(viewModel);
        window.Show();
        await viewModel.StartLocalAsync(home);
        Dispatcher.UIThread.RunJobs();

        Assert.Matches(@"· \d+x\d+$", viewModel.Status);
        var started = PtyHost.Default.List().Single(info => info.Home == home);
        window.Close();
        await viewModel.DisposeAsync();

        Assert.DoesNotContain(PtyHost.Default.List(), info => info.Id == started.Id);
        Assert.True(await ExitsAsync(started.Pid!.Value), $"shell pid {started.Pid} still running");
    });

    private static async Task<bool> ExitsAsync(int pid)
    {
        var deadline = DateTime.UtcNow + OutputTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            await Task.Delay(50);
        }
        return false;
    }

    /** 一边泵 UI 消息一边等屏幕上出现满足条件的行(含滚回), 超时则带上当前屏幕内容失败。 */
    private static async Task WaitForLineAsync(TerminalSession session, Func<string, bool> predicate)
    {
        var deadline = DateTime.UtcNow + OutputTimeout;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (Lines(session).Any(predicate))
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"expected line not found; screen:\n{string.Join('\n', Lines(session))}");
    }

    private static List<string> Lines(TerminalSession session)
    {
        lock (session.Gate)
        {
            var screen = session.Screen;
            var lines = new List<string>();
            for (var line = -screen.ScrollbackCount; line < screen.Height; line++)
                lines.Add(Text(screen.LineAt(line)));
            return lines;
        }
    }

    private static string Text(ReadOnlySpan<Cell> row)
    {
        var builder = new StringBuilder();
        foreach (var cell in row)
        {
            if (cell.Character != '\0')
                builder.Append(cell.Character);
        }
        return builder.ToString().TrimEnd();
    }

    private static void Capture(Window window, string name)
    {
        Directory.CreateDirectory(ScreenshotDirectory);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ScreenshotDirectory, $"{name}.png"), PngBitmapEncoderOptions.Default);
    }
}
