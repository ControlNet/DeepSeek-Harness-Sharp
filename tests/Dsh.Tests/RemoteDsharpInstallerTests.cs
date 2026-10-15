using System.Diagnostics;
using System.Runtime.Versioning;
using Dsh.Fleet;

namespace Dsh.Tests;

/** 远端 dsharp 自动部署: RID 映射、Release 资产 URL、版本复用规则, 以及停旧宿主的脚本在真实 shell 里只停该二进制的宿主。 */
public sealed class RemoteDsharpInstallerTests
{
    [Theory]
    [InlineData("Linux", "x86_64", "linux-x64")]
    [InlineData("linux", "amd64", "linux-x64")]
    [InlineData("Linux", "aarch64", "linux-arm64")]
    [InlineData("Darwin", "arm64", "osx-arm64")]
    [InlineData("Darwin", "x86_64", "osx-x64")]
    [InlineData("Windows", "x86_64", "win-x64")]
    [InlineData("Windows", "AMD64", "win-x64")]
    [InlineData("Windows", "arm64", "win-arm64")]
    public void Rid_Maps_Supported_Platforms(string os, string arch, string expected)
        => Assert.Equal(expected, RemoteDsharpInstaller.RidFor(os, arch));

    [Theory]
    [InlineData("plan9", "x86_64")]
    [InlineData("Linux", "mips")]
    [InlineData("Windows", "mips")]
    public void Rid_Returns_Null_For_Unsupported(string os, string arch)
        => Assert.Null(RemoteDsharpInstaller.RidFor(os, arch));

    [Fact]
    public void Asset_Url_Uses_Latest_By_Default()
    {
        var settings = new DeploySettings { ReleaseBaseUrl = "https://host/releases" };
        Assert.Equal("https://host/releases/latest/download/dsharp-linux-x64.tar.gz", RemoteDsharpInstaller.AssetUrl(settings, "linux-x64"));
    }

    [Fact]
    public void Asset_Url_Uses_Tag_For_Pinned_Version()
    {
        var settings = new DeploySettings { ReleaseBaseUrl = "https://host/releases/", ReleaseVersion = "1.2.3" };
        Assert.Equal("https://host/releases/download/v1.2.3/dsharp-linux-x64.tar.gz", RemoteDsharpInstaller.AssetUrl(settings, "linux-x64"));
        Assert.Equal("https://host/releases/download/v1.2.3/dsharp-osx-arm64.tar.gz", RemoteDsharpInstaller.AssetUrl(settings with { ReleaseVersion = "v1.2.3" }, "osx-arm64"));
    }

    /** 远端更旧或版本不可读才升级; 相同或更新则复用; 预发布/构建后缀不参与比较。 */
    [Theory]
    [InlineData("0.2.0", "0.2.0", true)]
    [InlineData("0.3.0", "0.2.0", true)]
    [InlineData("v0.2", "0.2.0", true)]
    [InlineData("0.2.0-rc1", "0.2.0", true)]
    [InlineData("0.1.9", "0.2.0", false)]
    [InlineData("0.2.0", "0.10.0", false)]
    [InlineData("", "0.2.0", false)]
    [InlineData(null, "0.2.0", false)]
    [InlineData("garbled", "0.2.0", false)]
    public void Installed_Version_Is_Reused_Only_When_Not_Older(string? installed, string current, bool reuse)
        => Assert.Equal(reuse, ReleaseVersions.CanReuse(installed, current));

    [Fact]
    public void Server_Download_Script_Downloads_Before_Stopping_The_Old_Host()
    {
        var stop = RemoteDsharpInstaller.StopHostCommand("/opt/old/dsharp");
        var script = RemoteDsharpInstaller.BuildServerDownloadScript(new DeploySettings { ReleaseBaseUrl = "https://host/releases" }, "linux-x64", stop);
        Assert.Contains("command -v curl", script);
        Assert.Contains("command -v wget", script);
        Assert.Contains("/latest/download/dsharp-linux-x64.tar.gz", script);
        Assert.Contains("exit 42", script);
        Assert.DoesNotContain("-x '", script);
        Assert.DoesNotContain(".dsh/run", script);
        Assert.True(script.IndexOf("curl", StringComparison.Ordinal) < script.IndexOf(stop, StringComparison.Ordinal));
        Assert.True(script.IndexOf(stop, StringComparison.Ordinal) < script.IndexOf("tar -xf", StringComparison.Ordinal));
    }

    [Fact]
    public void Server_Download_Script_Passes_Remote_Proxy()
    {
        var settings = new DeploySettings { ReleaseBaseUrl = "https://host/releases", Via = DownloadVia.Server, ServerProxy = "http://proxy.local:8080" };
        var script = RemoteDsharpInstaller.BuildServerDownloadScript(settings, "linux-x64", "");
        Assert.Contains("-x 'http://proxy.local:8080'", script);
        // wget 分支也必须带上代理, 否则只有 wget 的远端会直连失败。
        Assert.Contains("-e use_proxy=yes -e https_proxy='http://proxy.local:8080'", script);
    }

    [Fact]
    public void Stop_Command_Is_Empty_When_Nothing_Is_Installed()
        => Assert.Equal("", RemoteDsharpInstaller.StopHostCommand(null));

    /** 真实 shell: 只停“旧二进制路径 + host serve”的进程, 同机另一个 dsharp 的宿主与执行脚本的 shell 都不受影响。 */
    [Fact(Timeout = 30000)]
    public async Task Stop_Command_Stops_Only_The_Host_Of_That_Binary()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX shell only");
            return;
        }

        var root = TempTree.CreateDirectory("stop");
        try
        {
            var old = FakeHostBinary(Path.Combine(root, "old"));
            var other = FakeHostBinary(Path.Combine(root, "other"));
            using var oldHost = Process.Start(old, ["host", "serve"]);
            using var otherHost = Process.Start(other, ["host", "serve"]);
            await Task.Delay(200, TestContext.Current.CancellationToken);

            using var shell = Process.Start("/bin/sh", ["-c", RemoteDsharpInstaller.StopHostCommand(old) + "echo done"]);
            await shell.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, shell.ExitCode);
            Assert.True(oldHost.HasExited, "the host of the replaced binary must be stopped");
            Assert.False(otherHost.HasExited, "a host started from another binary must keep running");
            otherHost.Kill();
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** A stand-in executable whose command line looks like `<path> host serve` while it runs. */
    [UnsupportedOSPlatform("windows")]
    private static string FakeHostBinary(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "dsharp");
        File.WriteAllText(path, "#!/bin/sh\nwhile :; do sleep 1; done\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
