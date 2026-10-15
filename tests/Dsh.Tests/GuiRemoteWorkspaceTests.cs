using System.Formats.Tar;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;

namespace Dsh.Tests;

/**
 * 远程工作区: 用 GuiHeadless 在真实渲染的 GUI 里做交互(打开工作区菜单/悬停右拉/点击/设置页)
 * 并截图, 不抢占前台。
 * - UI 结构用例不需要服务器, 常规运行。
 * - 真实 SSH 连接用例需要 <repo>/artifacts020/ssh-verify.json(本地, 不进版本库), 缺失即跳过。
 */
public sealed class GuiRemoteWorkspaceTests
{
    private static readonly string ScreenshotDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts020", "gui-screenshots"));

    /** 远端会话列表很长时必须自己滚动, 不能溢出压住底部“终端/设置”(DockPanel 不会压缩已停靠子项)。 */
    [Fact]
    public async Task RemoteSessionList_IsBounded_AndKeepsFooterVisible() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        using var environmentScope = environment;
        var window = new MainWindow(environment.App, environment.Agent);
        var viewModel = window.ViewModel!;
        window.Width = 900;
        window.Height = 720;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var group = new RemoteWorkspaceGroupViewModel { Header = "pub@host:~/repo" };
        for (var index = 0; index < 40; index++)
        {
            group.Sessions.Add(new RemoteSessionNodeViewModel
            {
                SessionId = $"remote-{index}",
                Title = $"session-{index}",
                Summary = "fixture",
                RelativeTime = "1 天前"
            });
        }

        viewModel.RemoteSessionGroups.Add(group);
        viewModel.HasRemoteSessions = true;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();

        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(view => view.Name == "RemoteSessionsScroll");
        var footer = window.GetVisualDescendants().OfType<Button>()
            .First(button => button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "设置"));
        var scrollerBottom = scroller.TranslatePoint(new Point(0, scroller.Bounds.Height), window);
        var footerTop = footer.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(scrollerBottom);
        Assert.NotNull(footerTop);
        Assert.True(scroller.Bounds.Height <= RemoteSectionMaxHeight,
            $"远端会话区高度 {scroller.Bounds.Height:0.#} 超过上限, 会溢出到页面其他区域");
        Assert.True(scrollerBottom.Value.Y <= footerTop.Value.Y,
            $"远端会话区底部 {scrollerBottom.Value.Y:0.#} 压住了底部按钮 {footerTop.Value.Y:0.#}");
        Capture(window, "headless-remote-sessions-bounded");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    });

    /** 与 Themes/Tokens.axaml 的 Layout.RemoteSectionMaxHeight 保持一致(布局断言用)。 */
    private const double RemoteSectionMaxHeight = 260;

    private static IReadOnlyList<MachineSpec> SampleWorkspaces() =>
    [
        MachineSpec.Ssh("实验室 GPU", "gpu-box.internal", 22, "dev", SshAuth.Password) with { RemotePath = "~/dsh-deploy" },
        MachineSpec.Ssh("构建机", "build-box.internal", 22, "builder", SshAuth.Key) with { KeyPath = "~/.ssh/id_ed25519" },
    ];

    /** 工作区菜单里“最近工作区”与“打开新工作区…”之间应有“远程工作区”, 悬停右拉列出已保存条目; 设置页“远程”页签可编辑保存。 */
    [Fact]
    public async Task WorkspaceMenu_AndSettings_RemoteSurface() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        using var environmentScope = environment;
        new MachineStore(environment.App.Home).Save(FleetConfig.Empty with { Machines = SampleWorkspaces() });

        var window = new MainWindow(environment.App, environment.Agent);
        var viewModel = window.ViewModel!;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 打开工作区切换浮层。
        var hostButton = window.GetVisualDescendants().OfType<Button>()
            .First(button => button.Flyout is Flyout && Equals(ToolTip.GetTip(button), "切换新会话默认工作区"));
        var workspaceFlyout = (Flyout)hostButton.Flyout!;
        workspaceFlyout.ShowAt(hostButton);
        Dispatcher.UIThread.RunJobs();

        var menu = (StackPanel)workspaceFlyout.Content!;
        var headers = menu.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains(headers, text => text!.StartsWith("最近工作区", StringComparison.Ordinal));
        var remoteEntry = menu.GetVisualDescendants().OfType<Button>()
            .First(button => button.Content is "远程工作区");
        var newWorkspace = menu.GetVisualDescendants().OfType<Button>()
            .First(button => button.Content is "打开新工作区…");
        // 顺序: “远程工作区”夹在“最近工作区”列表与“打开新工作区…”之间。
        var visuals = menu.GetVisualDescendants().ToList();
        Assert.True(visuals.IndexOf(remoteEntry) < visuals.IndexOf(newWorkspace));
        Capture(window, "headless-workspace-menu");

        // 悬停“远程工作区”即向右拉出子菜单, 列出已保存工作区。
        Hover(window, remoteEntry);
        Dispatcher.UIThread.RunJobs();
        var remoteFlyout = (Flyout)remoteEntry.Flyout!;
        Assert.True(remoteFlyout.IsOpen, "悬停后“远程工作区”右拉菜单应打开");

        var items = ((Control)remoteFlyout.Content!).GetVisualDescendants().OfType<Button>()
            .Where(button => button.DataContext is RemoteWorkspaceChoiceViewModel)
            .ToList();
        Assert.Equal(SampleWorkspaces().Count, items.Count);
        Assert.Contains(items, item => item.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.Text == "实验室 GPU"));
        Capture(window, "headless-remote-workspace-flyout");

        remoteFlyout.Hide();
        workspaceFlyout.Hide();
        Dispatcher.UIThread.RunJobs();

        // 设置页“远程”页签: 列出连接, 编辑后保存落盘。
        viewModel.ShowSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var remoteSection = viewModel.Preferences.Sections.First(section => section.Section == SettingsSection.Remote);
        remoteSection.SelectCommand!.Execute(remoteSection);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.Preferences.IsRemoteSection);
        Assert.Equal(SampleWorkspaces().Count, viewModel.Preferences.RemoteWorkspaces.Count);
        Assert.NotNull(viewModel.Preferences.SelectedRemoteWorkspace);
        Capture(window, "headless-settings-remote");

        // “远端目录”右侧的文件夹按钮 + “测试连接”按钮。
        Assert.NotNull(window.GetVisualDescendants().OfType<Button>()
            .First(button => Equals(ToolTip.GetTip(button), "选择远端机器上的目录")));
        Assert.NotNull(window.GetVisualDescendants().OfType<Button>()
            .First(button => button.Content is "测试连接"));

        // 右下角气泡: 成功与失败各一条。
        viewModel.ShowToast("连接成功", "已连接 dev@gpu-box.internal（linux）· 远端目录 ~/dsh-deploy", false);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(viewModel.Toasts);
        Capture(window, "headless-toast-success");
        viewModel.ShowToast("连接失败", "ssh dev@gpu-box.internal 失败: Permission denied (publickey).", true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, viewModel.Toasts.Count);
        Capture(window, "headless-toast-both");

        viewModel.Preferences.SelectedRemoteWorkspace!.Name = "改名后的工作区";
        viewModel.Preferences.SaveRemoteWorkspacesCommand.Execute(null);
        Assert.Contains(new MachineStore(environment.App.Home).Load().Machines,
            workspace => workspace.Name == "改名后的工作区");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    });

    /** 真实 SSH: 从工作区菜单点击已保存的远程工作区, 经 ssh 起远端宿主并接入。 */
    [Fact]
    public async Task RemoteWorkspace_ConnectsOverSsh()
    {
        var spec = LoadSpec();
        if (spec is null)
        {
            Assert.Skip("未找到 artifacts020/ssh-verify.json, 跳过真实 SSH 验证");
            return;
        }

        await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            using var environmentScope = environment;
            new MachineStore(environment.App.Home).Save(FleetConfig.Empty with { Machines = [spec.ToMachine()] });

            var window = new MainWindow(environment.App, environment.Agent);
            var viewModel = window.ViewModel!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var hostButton = window.GetVisualDescendants().OfType<Button>()
                .First(button => button.Flyout is Flyout && Equals(ToolTip.GetTip(button), "切换新会话默认工作区"));
            var workspaceFlyout = (Flyout)hostButton.Flyout!;
            workspaceFlyout.ShowAt(hostButton);
            Dispatcher.UIThread.RunJobs();
            var menu = (StackPanel)workspaceFlyout.Content!;
            var remoteEntry = menu.GetVisualDescendants().OfType<Button>()
                .First(button => button.Content is "远程工作区");
            Hover(window, remoteEntry);
            Dispatcher.UIThread.RunJobs();
            var item = ((Control)((Flyout)remoteEntry.Flyout!).Content!).GetVisualDescendants().OfType<Button>()
                .First(button => button.DataContext is RemoteWorkspaceChoiceViewModel);
            Click(window, item);

            for (var attempt = 0; attempt < 450; attempt++)
            {
                var status = viewModel.StatusText;
                if (status.Contains("已连接", StringComparison.Ordinal) || status.Contains("失败", StringComparison.Ordinal))
                    break;
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(viewModel.StatusText.Contains("已连接", StringComparison.Ordinal),
                $"{spec.Name} 连接失败: {viewModel.StatusText}");
            Assert.NotNull(viewModel.RemoteHost);
            Capture(window, "headless-remote-workspace-connected");

            // 远端目录列举(供“选择远端目录”): 真实走 ssh 通道, 家目录应至少含目录项。
            var home = await viewModel.RemoteHost!.ListDirectoryAsync("~", TestContext.Current.CancellationToken);
            Assert.True(home.Entries.Count > 0, $"远端 {spec.Host} 家目录列举为空");
            Assert.Contains(home.Entries, entry => entry.IsDirectory);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /** 设置页“测试连接”走完整 GUI 路径(行VM→ToModel→启动器): 密码认证应成功并弹成功气泡。 */
    [Fact]
    public async Task SettingsTestConnection_PasswordAuth_Succeeds()
    {
        var spec = LoadSpec();
        if (spec is null)
        {
            Assert.Skip("未找到 artifacts020/ssh-verify.json, 跳过真实 SSH 验证");
            return;
        }

        await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            using var environmentScope = environment;
            var window = new MainWindow(environment.App, environment.Agent);
            var viewModel = window.ViewModel!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var row = new SshWorkspaceRowViewModel
            {
                Name = spec.Name,
                Host = spec.Host,
                Port = spec.Port.ToString(),
                User = spec.User,
                Auth = spec.Auth,
                Password = spec.Password,
                RemotePath = spec.RemotePath,
                Owner = viewModel.Preferences,
            };
            viewModel.Preferences.RemoteWorkspaces.Add(row);
            viewModel.Preferences.SelectedRemoteWorkspace = row;

            await row.TestConnectionCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(viewModel.Toasts, toast => toast.IsSuccess && toast.Title == "连接成功");
            Assert.DoesNotContain(viewModel.Toasts, toast => toast.IsError);
            Capture(window, "headless-settings-test-connection");

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /** 自动部署: 远端没有 dsharp 时把本机发布打包上传到 ~/.dsharp/ 并按绝对路径起宿主(VS Code 式, 不注册命令)。 */
    [Fact]
    public async Task AutoDeploy_Uploads_And_Hosts_On_Real_Server()
    {
        var spec = LoadSpec();
        if (spec is null)
        {
            Assert.Skip("未找到 artifacts020/ssh-verify.json, 跳过真实 SSH 验证");
            return;
        }

        var publish = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/deploy/dsh"));
        if (!Directory.Exists(publish))
        {
            Assert.Skip($"缺少本地发布目录: {publish}");
            return;
        }

        var tarPath = Path.Combine(Path.GetTempPath(), $"dsharp-deploy-{Guid.NewGuid():N}.tar");
        TarFile.CreateFromDirectory(publish, tarPath, includeBaseDirectory: false);
        try
        {
            var workspace = spec.ToMachine();
            var result = await RemoteDsharpInstaller.EnsureAsync(
                workspace,
                new DeploySettings { ReleaseBaseUrl = "https://example.invalid", Via = DownloadVia.Client },
                null,
                TestContext.Current.CancellationToken,
                (_, _, _) => Task.FromResult(tarPath));
            Assert.EndsWith("/dsharp", result.CommandPath);
            Assert.Contains("/.dsharp/", result.CommandPath.Replace('\\', '/'));

            var summary = await SshHostLauncher.TestAsync(workspace, result.CommandPath, GuiSettings.RemoteClientName, TestContext.Current.CancellationToken);
            Assert.Contains("已连接", summary);
        }
        finally
        {
            File.Delete(tarPath);
        }
    }

    private static Spec? LoadSpec()
    {
        var file = Path.Combine(Path.GetDirectoryName(ScreenshotDirectory)!, "ssh-verify.json");
        if (!File.Exists(file))
            return null;
        var specs = JsonSerializer.Deserialize<List<Spec>>(
            File.ReadAllText(file),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return specs?.FirstOrDefault();
    }

    /** 用真实的无头鼠标移动触发控件的 PointerEntered(悬停)。 */
    private static void Hover(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        if (center is { } point)
            window.MouseMove(point);
    }

    /** 用真实的无头鼠标点击控件(会走 Button.OnClick, 从而执行 Command)。 */
    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        if (center is { } point)
        {
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
    }

    private static void Capture(Window window, string name)
    {
        try
        {
            Directory.CreateDirectory(ScreenshotDirectory);
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(ScreenshotDirectory, $"{name}.png"), PngBitmapEncoderOptions.Default);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"screenshot {name} failed: {error.Message}");
        }
    }

    public sealed record Spec(
        string Name,
        string Host,
        int Port,
        string User,
        string Auth,
        string? Password = null,
        string? KeyPath = null,
        string? Proxy = null,
        string? RemotePath = null)
    {
        public MachineSpec ToMachine() => MachineSpec.Ssh(Name, Host, Port, User, Auth) with { RemotePath = RemotePath, Password = Password };
    }
}
