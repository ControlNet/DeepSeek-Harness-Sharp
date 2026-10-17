using Dsh.Boot;
using Dsh.Fleet;

namespace Dsh.Tests;

/** 机器清单: GUI、CLI 与 WebBridge 只读写 settings.yaml 的 fleet 段。 */
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loading_UsesOnlyFleetSettings_AndLeavesUnrelatedFilesAndPluginParametersUntouched(bool configured)
    {
        var home = new HarnessHome(_root);
        var machine = MachineSpec.Ssh("keep", "keep.internal", 22, "dev", SshAuth.Agent);
        var store = new MachineStore(home);
        File.WriteAllText(home.SettingsFile, """

            plugins:
              "@deepseek-ai/dsh-gui":
                remoteWorkspaces: [{ name: ignored, host: ignored.internal }]
                remoteAutoDeploy: false
                remoteDownloadProxy: http://ignored-proxy:3128
            """);
        if (configured)
            store.Save(new FleetConfig([machine], new DeploySettings { ServerProxy = "http://remote-proxy:3128" }));
        var original = File.ReadAllText(home.SettingsFile);
        var unrelatedFile = Path.Combine(_root, "fleet", "machines.json");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedFile)!);
        const string unrelatedContents = "{not a valid configuration";
        File.WriteAllText(unrelatedFile, unrelatedContents);

        var config = store.Load();

        if (configured)
            Assert.Equal(machine, Assert.Single(config.Machines));
        else
            Assert.Empty(config.Machines);
        Assert.True(config.Deploy.AutoDeploy);
        Assert.Equal(configured ? "http://remote-proxy:3128" : null, config.Deploy.ServerProxy);
        Assert.Null(HarnessSettings.Load(home).Network?.OutboundProxy);
        Assert.Equal(original, File.ReadAllText(home.SettingsFile));
        Assert.Equal(unrelatedContents, File.ReadAllText(unrelatedFile));
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
    }

}
