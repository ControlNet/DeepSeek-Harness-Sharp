using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 机器管理页的视图模型: 经真实 MachineStore(settings.yaml 的 fleet 段)增删改机器与部署设置, 对真实 host 测试连接与诊断。 */
public sealed class MachinesViewModelTests : IDisposable
{
    private readonly string _home = TempTree.CreateDirectory("machines-vm");

    public void Dispose() => TempTree.Delete(_home);

    [Fact]
    public async Task Machines_AreAddedRenamedAndDeleted_AndEveryFieldRoundTripsThroughTheStore()
    {
        var store = new MachineStore(new HarnessHome(_home));
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        var changes = 0;
        viewModel.MachinesChanged += () => changes++;
        viewModel.Load();
        var initial = (Assert.Single(viewModel.Machines).IsImplicit, viewModel.Editor, viewModel.DeleteCommand.CanExecute(null));

        viewModel.NewSshCommand.Execute(null);
        Fill(viewModel.Editor!, "build", SshAuth.Key);
        await viewModel.SaveCommand.ExecuteAsync(null);
        var saved = Assert.Single(store.Load().Machines);

        viewModel.Editor!.Name = "builder";
        viewModel.Editor.Port = "22";
        await viewModel.SaveCommand.ExecuteAsync(null);
        var renamed = store.Load().Machines.Select(machine => (machine.Name, machine.Port)).ToList();

        viewModel.NewSshCommand.Execute(null);
        Fill(viewModel.Editor!, "lab", SshAuth.Password);
        await viewModel.SaveCommand.ExecuteAsync(null);
        viewModel.Editor!.Name = "builder";
        await viewModel.SaveCommand.ExecuteAsync(null);
        var duplicate = (viewModel.StatusIsError, string.Join(",", store.Load().Machines.Select(machine => machine.Name)));

        viewModel.Selected = viewModel.Machines.Single(row => row.Name == "lab");
        viewModel.DeleteCommand.Execute(null);
        var beforeConfirming = store.Load().Machines.Count;
        await viewModel.Confirmation!.AcceptCommand.ExecuteAsync(null);

        Assert.Equal((true, (MachineEditorViewModel?)null, false), initial);
        Assert.Equal(("build", MachineKinds.Ssh, "build.internal", 2222, "dev", SshAuth.Key), (saved.Name, saved.Kind, saved.Host, saved.Port, saved.User, saved.Auth));
        Assert.Equal(("~/.ssh/id_test", null, "ssh -W %h:%p jump", "/srv/work", "~/.dsh-build"), (saved.KeyPath, saved.Password, saved.Proxy, saved.RemotePath, saved.HarnessHome));
        Assert.Equal(["ConnectTimeout=5", "ServerAliveInterval=10"], saved.SshOptions);
        Assert.Equal([("builder", 22)], renamed);
        Assert.Equal((true, "builder,lab"), duplicate);
        Assert.Equal(2, beforeConfirming);
        Assert.Equal(["local", "builder"], viewModel.Machines.Select(row => row.Name));
        Assert.Equal("builder", Assert.Single(store.Load().Machines).Name);
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task DeploySettings_AreSavedSeparately_AndSurviveSavingAMachine()
    {
        var store = new MachineStore(new HarnessHome(_home));
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        viewModel.Load();
        var deploy = viewModel.Deploy!;

        (deploy.AutoDeploy, deploy.ReleaseVersion, deploy.Via, deploy.ServerProxy) = (false, "v1.2.3", DownloadVia.Client, " ");
        await viewModel.SaveDeploySettingsCommand.ExecuteAsync(null);
        deploy.ReleaseVersion = "v9.9.9";
        viewModel.NewSshCommand.Execute(null);
        Fill(viewModel.Editor!, "build", SshAuth.Agent);
        await viewModel.SaveCommand.ExecuteAsync(null);
        var config = store.Load();

        Assert.Equal((false, "v1.2.3", DownloadVia.Client, (string?)null), (config.Deploy.AutoDeploy, config.Deploy.ReleaseVersion, config.Deploy.Via, config.Deploy.ServerProxy));
        Assert.Equal(DeploySettings.DefaultReleaseBaseUrl, config.Deploy.ReleaseBaseUrl);
        Assert.Same(deploy, viewModel.Deploy);
        Assert.Equal("v9.9.9", viewModel.Deploy!.ReleaseVersion);
        Assert.Equal(("build", (string?)null), (Assert.Single(config.Machines).Name, config.Machines[0].KeyPath));
    }

    [Fact(Timeout = 60000)]
    public async Task TestConnection_ShowsHostVersionAndPlatform_AndDoctorListsTheChecks()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mvt", TestContext.Current.CancellationToken);
        // The fixture's listener starts in the background; a probe connection (which retries) waits until it accepts, so the single test attempt below meets a ready host.
        await host.ConnectAsync("probe", TestContext.Current.CancellationToken);
        var viewModel = new MachinesViewModel(new MachineStore(new HarnessHome(_home)), FleetConnectionTests.TestOptions);
        viewModel.Load();
        viewModel.NewLocalCommand.Execute(null);
        (viewModel.Editor!.Name, viewModel.Editor.HarnessHome) = ("alpha", host.Root);
        await viewModel.SaveCommand.ExecuteAsync(null);

        await viewModel.TestConnectionCommand.ExecuteAsync(null);
        var row = viewModel.Machines.Single(machine => machine.Name == "alpha");
        var tested = (row.StateText, row.HostVersion, row.Platform is { Length: > 0 }, viewModel.StatusIsError ? viewModel.Status : null);
        await viewModel.RunDoctorCommand.ExecuteAsync(null);
        var checks = viewModel.DoctorChecks.ToDictionary(check => check.Name, check => check.Outcome);

        viewModel.NewLocalCommand.Execute(null);
        (viewModel.Editor!.Name, viewModel.Editor.HarnessHome) = ("nowhere", Path.Combine(_home, "no-host"));
        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(("已连接", RemoteHostServer.ReleaseVersion, true, (string?)null), tested);
        Assert.Contains(viewModel.OperationLog, line => line.StartsWith($"alpha: host {RemoteHostServer.ReleaseVersion}", StringComparison.Ordinal));
        Assert.Equal(DoctorOutcome.Ok, checks["platform"]);
        Assert.Equal(DoctorOutcome.Ok, checks["connection"]);
        Assert.True(viewModel.StatusIsError, viewModel.Status);
        Assert.Contains("nowhere", viewModel.Status, StringComparison.Ordinal);
    }

    private static void Fill(MachineEditorViewModel editor, string name, string auth)
    {
        (editor.Name, editor.Host, editor.Port, editor.User, editor.Auth) = (name, $"{name}.internal", "2222", "dev", auth);
        (editor.KeyPath, editor.Password, editor.Proxy) = ("~/.ssh/id_test", "secret", "ssh -W %h:%p jump");
        (editor.RemotePath, editor.HarnessHome, editor.SshOptions) = ("/srv/work", "~/.dsh-build", "ConnectTimeout=5\r\n\r\n ServerAliveInterval=10 \n");
    }
}
