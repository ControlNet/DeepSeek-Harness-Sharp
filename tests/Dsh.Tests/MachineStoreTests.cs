using System.Text.Json;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Gui.Services;

namespace Dsh.Tests;

/** 机器清单: GUI、CLI 与 WebBridge 共用 settings.yaml 的 fleet 段, 以及从 fleet/machines.json 和旧 GUI 设置里一次性迁移。 */
public sealed class MachineStoreTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("fleet-store");

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void Machines_RoundTrip_With_All_Fields_In_The_OwnerOnly_Settings_File()
    {
        var store = new MachineStore(new HarnessHome(_root));
        MachineSpec[] machines =
        [
            MachineSpec.Ssh("build", "build-box.internal", 22, "builder", SshAuth.Password) with { Proxy = "nc -x 127.0.0.1:1080 %h %p", RemotePath = "~/work", Password = "null" },
            MachineSpec.Ssh("gpu", "gpu-box.internal", 2222, "dev", SshAuth.Key) with { KeyPath = "C:/keys/id_ed25519", HarnessHome = "~/.dsh-gpu", SshOptions = ["UserKnownHostsFile=/tmp/kh"] },
            MachineSpec.Local("/srv/second-home", "second"),
            MachineSpec.Ssh("home", "", 22, "dev", SshAuth.Key) with
            {
                Kind = MachineKinds.Iroh,
                EndpointId = new string('a', IrohSshTransport.EndpointIdLength),
                RelayUrls = ["https://relay.example.org/"],
                KeyPath = "/keys/home",
            },
        ];
        store.Save(new FleetConfig(machines, new DeploySettings { AutoDeploy = false, Via = DownloadVia.Client, ReleaseVersion = "v0.2.0" }));

        var reloaded = store.Load();
        Assert.Equal(machines.Length, reloaded.Machines.Count);
        Assert.Equal(machines[0], reloaded.Machines[0]);
        Assert.Equal(machines[1] with { SshOptions = null }, reloaded.Machines[1] with { SshOptions = null });
        Assert.Equal(["UserKnownHostsFile=/tmp/kh"], reloaded.Machines[1].SshOptions);
        Assert.Equal(machines[2], reloaded.Machines[2]);
        Assert.Equal(machines[3] with { RelayUrls = null }, reloaded.Machines[3] with { RelayUrls = null });
        Assert.Equal(machines[3].RelayUrls, reloaded.Machines[3].RelayUrls);
        Assert.False(reloaded.Deploy.AutoDeploy);
        Assert.Equal(DownloadVia.Client, reloaded.Deploy.Via);
        Assert.Equal([MachineSpec.LocalName, "build", "gpu", "second", "home"], reloaded.AllMachines().Select(machine => machine.Name));
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
        Assert.Null(HarnessSettings.Load(home).Network?.OutboundProxy);
        var gui = HarnessSettings.Load(home).Plugins[GuiSettings.Package].Parameters;
        Assert.False(gui.ContainsKey("remoteWorkspaces"));
        Assert.False(gui.ContainsKey("remoteAutoDeploy"));
        Assert.False(gui.ContainsKey("remoteDownloadProxy"));
        Assert.Equal(GuiSettings.ThemeLight, new GuiSettings(home).Load().Theme);
        Assert.NotNull(HarnessSettings.Load(home).Fleet);
        Assert.Equal(imported.Machines, new MachineStore(home).Load().Machines);
    }

    [Theory]
    [InlineData("")]
    [InlineData("remoteWorkspaces: null")]
    [InlineData("remoteWorkspaces: []")]
    public void LegacyGuiEmptyWorkspaces_AreMigratedWithoutRejectingOptionalConfiguration(string workspaces)
    {
        var home = new HarnessHome(_root);
        File.WriteAllText(home.SettingsFile, $"""
            plugins:
              "@deepseek-ai/dsh-gui":
                theme: light
                remoteAutoDeploy: false
                {workspaces}
            """);
        var store = new MachineStore(home);

        var imported = store.Load();

        Assert.Empty(imported.Machines);
        Assert.False(imported.Deploy.AutoDeploy);
        Assert.NotNull(HarnessSettings.Load(home).Fleet);
        Assert.Equal(GuiSettings.ThemeLight, new GuiSettings(home).Load().Theme);
        Assert.Empty(store.Load().Machines);
    }

    [Theory]
    [InlineData("remoteWorkspaces: invalid")]
    [InlineData("remoteWorkspaces: [invalid]")]
    [InlineData("remoteWorkspaces: [{ name: lab, host: lab.internal }, { name: incomplete }]")]
    public void InvalidLegacyGuiWorkspaces_FailWithoutRemovingTheOriginalConfiguration(string workspaces)
    {
        var home = new HarnessHome(_root);
        var original = $"""
            plugins:
              "@deepseek-ai/dsh-gui":
                theme: light
                {workspaces}
            """;
        File.WriteAllText(home.SettingsFile, original);

        Assert.Throws<InvalidDataException>(() => new MachineStore(home).Load());

        Assert.Equal(original, File.ReadAllText(home.SettingsFile));
    }

    /** 早期版本的 fleet/machines.json: 首次加载时写进 settings.yaml 的 fleet 段并删除该文件。 */
    [Fact]
    public void Machines_Json_Is_Imported_Into_Settings_And_Deleted()
    {
        var home = new HarnessHome(_root);
        var store = new MachineStore(home);
        Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyFilePath)!);
        File.WriteAllText(store.LegacyFilePath, """
            {
              "machines": [
                { "name": "build", "kind": "ssh", "host": "build.internal", "port": 2200, "user": "dev", "auth": "key", "keyPath": "~/.ssh/id_ed25519" },
                { "name": "second", "kind": "local", "harnessHome": "/srv/second-home" }
              ],
              "deploy": { "autoDeploy": false, "via": "client" }
            }
            """);

        var imported = store.Load();

        Assert.False(File.Exists(store.LegacyFilePath));
        Assert.Equal(
            [MachineSpec.Ssh("build", "build.internal", 2200, "dev", SshAuth.Key) with { KeyPath = "~/.ssh/id_ed25519" }, MachineSpec.Local("/srv/second-home", "second")],
            imported.Machines);
        Assert.Equal((false, DownloadVia.Client), (imported.Deploy.AutoDeploy, imported.Deploy.Via));
        Assert.Equal(imported.Machines, store.Load().Machines);
        Assert.Equal(2, HarnessSettings.Load(home).Fleet!.Machines.Count);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    public void InvalidLegacyMachinesFile_FailsWithoutDeletingItOrChangingSettings(string contents)
    {
        var home = new HarnessHome(_root);
        var store = new MachineStore(home);
        store.Save(new FleetConfig([MachineSpec.Ssh("keep", "keep.internal", 22, "dev", SshAuth.Agent)], new DeploySettings()));
        var settingsBefore = File.ReadAllText(home.SettingsFile);
        Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyFilePath)!);
        File.WriteAllText(store.LegacyFilePath, contents);

        Assert.Throws<JsonException>(() => store.Load());

        Assert.Equal(contents, File.ReadAllText(store.LegacyFilePath));
        Assert.Equal(settingsBefore, File.ReadAllText(home.SettingsFile));
    }

    /** 只改写 fleet 段: 注释、其它段与 CRLF 换行原样保留; 删掉最后一台机器后文件回到原样。 */
    [Fact]
    public void Saving_Rewrites_Only_The_Fleet_Section_And_Keeps_Comments_And_Crlf()
    {
        var home = new HarnessHome(_root);
        Directory.CreateDirectory(_root);
        var original = string.Join("\r\n",
            "# my settings",
            "global_default_model: deepseek-official/deepseek-v4-flash  # keep this comment",
            "",
            "# providers I use",
            "providers:",
            "  deepseek-official:",
            "    type: deepseek",
            "plugins:",
            "  \"@deepseek-ai/dsh-memory\": false",
            "");
        File.WriteAllText(home.SettingsFile, original);
        var store = new MachineStore(home);

        store.Upsert(MachineSpec.Ssh("box", "box.internal", 22, "me", SshAuth.Agent));
        var saved = File.ReadAllText(home.SettingsFile);

        Assert.StartsWith(original.TrimEnd(), saved, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", saved.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("fleet:", saved, StringComparison.Ordinal);
        Assert.Equal("box.internal", Assert.Single(store.Load().Machines).Host);

        Assert.True(store.Remove("box"));
        Assert.Equal(original, File.ReadAllText(home.SettingsFile));
    }

    [Fact]
    public void Loading_Without_Any_Configuration_Creates_Nothing()
    {
        var home = new HarnessHome(_root);
        Assert.Empty(new MachineStore(home).Load().Machines);
        Assert.False(File.Exists(home.SettingsFile));
        Assert.False(File.Exists(new MachineStore(home).LegacyFilePath));
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
