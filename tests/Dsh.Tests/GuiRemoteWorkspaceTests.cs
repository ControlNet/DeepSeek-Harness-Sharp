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
 * 远程工作区: 用 GuiHeadless 在真实渲染的 GUI 里做交互(打开工作区菜单/设置页/工作台)
 * 并截图, 不抢占前台。
 * - UI 结构用例不需要服务器, 常规运行。
 * - 真实 SSH 连接用例需要 <repo>/artifacts020/ssh-verify.json(本地, 不进版本库), 缺失即跳过。
 */
public sealed class GuiRemoteWorkspaceTests
{
    private static readonly string ScreenshotDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts020", "gui-screenshots"));

    private static IReadOnlyList<MachineSpec> SampleWorkspaces() =>
    [
        MachineSpec.Ssh("实验室 GPU", "gpu-box.internal", 22, "dev", SshAuth.Password) with { RemotePath = "~/dsh-deploy" },
        MachineSpec.Ssh("构建机", "build-box.internal", 22, "builder", SshAuth.Key) with { KeyPath = "~/.ssh/id_ed25519" },
    ];

    /** 工作区菜单只管本地新会话的默认目录(最近工作区 + 打开新工作区…); 机器在工作台里统一显示; 设置页“远程”页签可编辑保存。 */
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
        Assert.Contains(menu.GetVisualDescendants().OfType<Button>(), button => button.Content is "打开新工作区…");
        Assert.DoesNotContain(menu.GetVisualDescendants().OfType<Button>(), button => button.Content is "远程工作区");
        Capture(window, "headless-workspace-menu");
        workspaceFlyout.Hide();
        Dispatcher.UIThread.RunJobs();

        // 保存的 SSH 机器出现在工作台的机器分组里(这里不可达, 只验证分组存在)。
        Assert.All(SampleWorkspaces(), machine => Assert.Contains(viewModel.Workspace.Dashboard.Machines, group => group.Name == machine.Name));

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

    /** 真实 SSH: 保存的机器由工作台的 Fleet 连接(必要时自动部署), 连上后经同一连接列举远端目录。 */
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

            var machine = viewModel.Workspace.Fleet.Find(spec.Name)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var lease = await machine.WaitConnectedAsync(timeout.Token);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(viewModel.Workspace.Dashboard.Machines, group => group.Name == spec.Name && group.IsConnected);
            Capture(window, "headless-remote-workspace-connected");

            // 远端目录列举(供“选择远端目录”): 真实走 ssh 通道, 家目录应至少含目录项。
            var home = await lease.Client.ListDirectoryAsync("~", TestContext.Current.CancellationToken);
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
