using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Gui.Services;

namespace Dsh.Tests;

/** 机器清单: GUI 与 CLI 共用的 fleet/machines.json, 以及从旧 GUI 设置里一次性迁移远程工作区。 */
public sealed class MachineStoreTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("fleet-store");

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void Machines_RoundTrip_With_All_Fields_In_An_OwnerOnly_File()
    {
        var store = new MachineStore(new HarnessHome(_root));
        MachineSpec[] machines =
        [
            MachineSpec.Ssh("build", "build-box.internal", 22, "builder", SshAuth.Password) with { Proxy = "nc -x 127.0.0.1:1080 %h %p", RemotePath = "~/work", Password = "s3cret" },
            MachineSpec.Ssh("gpu", "gpu-box.internal", 2222, "dev", SshAuth.Key) with { KeyPath = "C:/keys/id_ed25519", HarnessHome = "~/.dsh-gpu", SshOptions = ["UserKnownHostsFile=/tmp/kh"] },
            MachineSpec.Local("/srv/second-home", "second"),
        ];
        store.Save(new FleetConfig(machines, new DeploySettings { AutoDeploy = false, Via = DownloadVia.Client, ReleaseVersion = "v0.2.0" }));

        var reloaded = store.Load();
        Assert.Equal(machines.Length, reloaded.Machines.Count);
        Assert.Equal(machines[0], reloaded.Machines[0]);
        Assert.Equal(machines[1] with { SshOptions = null }, reloaded.Machines[1] with { SshOptions = null });
        Assert.Equal(["UserKnownHostsFile=/tmp/kh"], reloaded.Machines[1].SshOptions);
        Assert.Equal(machines[2], reloaded.Machines[2]);
        Assert.False(reloaded.Deploy.AutoDeploy);
        Assert.Equal(DownloadVia.Client, reloaded.Deploy.Via);
        Assert.Equal([MachineSpec.LocalName, "build", "gpu", "second"], reloaded.AllMachines().Select(machine => machine.Name));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
    }

    [Fact]
    public void Upsert_Replaces_By_Name_And_Remove_Reports_Missing_Machines()
    {
        var store = new MachineStore(new HarnessHome(_root));
        store.Upsert(MachineSpec.Ssh("box", "a.internal", 22, "me", SshAuth.Agent));
        store.Upsert(MachineSpec.Ssh("box", "b.internal", 22, "me", SshAuth.Agent));

        Assert.Equal("b.internal", Assert.Single(store.Load().Machines).Host);
        Assert.True(store.Remove("box"));
        Assert.False(store.Remove("box"));
        Assert.Empty(store.Load().Machines);
    }

    [Theory]
    [InlineData(MachineSpec.LocalName)]
    [InlineData("a/b")]
    [InlineData("")]
    public void Reserved_Or_Invalid_Names_Are_Rejected(string name)
    {
        var store = new MachineStore(new HarnessHome(_root));
        Assert.Throws<InvalidOperationException>(() => store.Upsert(MachineSpec.Ssh(name, "host", 22, "me", SshAuth.Agent)));
    }

    [Fact]
    public void Duplicate_Names_Are_Rejected()
    {
        var store = new MachineStore(new HarnessHome(_root));
        var machine = MachineSpec.Ssh("box", "host", 22, "me", SshAuth.Agent);
        Assert.Throws<InvalidOperationException>(() => store.Save(FleetConfig.Empty with { Machines = [machine, machine] }));
    }

    /** 旧版 GUI 把远程工作区放在 settings.yaml 的 GUI 插件参数里: 首次加载时搬进机器清单, 并从 settings.yaml 删掉, 其余 GUI 参数保持。 */
    [Fact]
    public void Legacy_Gui_Workspaces_Are_Imported_Once_And_Removed_From_Settings()
    {
        var home = new HarnessHome(_root);
        WriteLegacyGuiSettings(home);

        var imported = new MachineStore(home).Load();

        var machine = Assert.Single(imported.Machines);
        Assert.Equal(MachineSpec.Ssh("lab", "lab.internal", 2200, "dev", SshAuth.Password) with { RemotePath = "~/repo", Password = "pw" }, machine);
        Assert.False(imported.Deploy.AutoDeploy);
        Assert.Equal("http://proxy:3128", imported.Deploy.DownloadProxy);
        var gui = HarnessSettings.Load(home).Plugins[GuiSettings.Package].Parameters;
        Assert.False(gui.ContainsKey("remoteWorkspaces"));
        Assert.False(gui.ContainsKey("remoteAutoDeploy"));
        Assert.Equal(GuiSettings.ThemeLight, new GuiSettings(home).Load().Theme);
        Assert.True(File.Exists(new MachineStore(home).FilePath));
        Assert.Equal(imported.Machines, new MachineStore(home).Load().Machines);
    }

    [Fact]
    public void Loading_Without_Any_Configuration_Creates_Nothing()
    {
        var home = new HarnessHome(_root);
        Assert.Empty(new MachineStore(home).Load().Machines);
        Assert.False(File.Exists(home.SettingsFile));
        Assert.False(File.Exists(new MachineStore(home).FilePath));
    }

    private static void WriteLegacyGuiSettings(HarnessHome home)
    {
        var settings = HarnessSettings.Load(home);
        settings.Plugins[GuiSettings.Package] = new PluginSetting
        {
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["theme"] = GuiSettings.ThemeLight,
                ["remoteAutoDeploy"] = false,
                ["remoteDownloadProxy"] = "http://proxy:3128",
                ["remoteWorkspaces"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = "lab",
                        ["host"] = "lab.internal",
                        ["port"] = 2200,
                        ["user"] = "dev",
                        ["auth"] = SshAuth.Password,
                        ["remotePath"] = "~/repo",
                        ["password"] = "pw",
                    },
                },
            },
        };
        settings.SavePlugins(home);
    }
}
