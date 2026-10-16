using Dsh.Runtime;
using Dsh.Gui.Services;

namespace Dsh.Tests;

/** 远端 dsharp 自动部署: RID 映射与 Release 资产 URL 的纯逻辑。 */
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
        var options = new RemoteDsharpInstaller.Options("https://host/releases", "latest", new OutboundProxy());
        Assert.Equal("https://host/releases/latest/download/dsharp-linux-x64.tar.gz", RemoteDsharpInstaller.AssetUrl(options, "linux-x64"));
    }

    [Fact]
    public void Asset_Url_Uses_Tag_For_Pinned_Version()
    {
        var options = new RemoteDsharpInstaller.Options("https://host/releases/", "1.2.3", new OutboundProxy());
        Assert.Equal("https://host/releases/download/v1.2.3/dsharp-linux-x64.tar.gz", RemoteDsharpInstaller.AssetUrl(options, "linux-x64"));
        Assert.Equal("https://host/releases/download/v1.2.3/dsharp-osx-arm64.tar.gz", RemoteDsharpInstaller.AssetUrl(options with { Version = "v1.2.3" }, "osx-arm64"));
    }

    [Fact]
    public void Server_Download_Script_Uses_Curl_Then_Wget()
    {
        var options = new RemoteDsharpInstaller.Options("https://host/releases", "latest", new OutboundProxy());
        var script = RemoteDsharpInstaller.BuildServerDownloadScript(options, "linux-x64");
        Assert.Contains("command -v curl", script);
        Assert.Contains("command -v wget", script);
        Assert.Contains("dsharp-linux-x64.tar.gz", script);
        Assert.Contains("/latest/download/dsharp-linux-x64.tar.gz", script);
        Assert.Contains("exit 42", script);
        Assert.DoesNotContain("-x '", script);
        // 部署必须停掉常驻宿主: 否则新二进制不会被 daemon 加载(回放/列表按旧协议工作)。
        Assert.Contains("pkill -f '[h]ost serve'", script);
        // 部署标记改由客户端按 Release 号写入, 脚本不再自带常量版本号。
        Assert.DoesNotContain(".dsharp-version", script);
    }

    [Fact]
    public void Server_Download_Script_Passes_Remote_Proxy()
    {
        var options = new RemoteDsharpInstaller.Options("https://host/releases", "latest", new OutboundProxy(), "server", "http://proxy.local:8080");
        var script = RemoteDsharpInstaller.BuildServerDownloadScript(options, "linux-x64");
        Assert.Contains("-x 'http://proxy.local:8080'", script);
        // wget 分支也必须带上代理, 否则只有 wget 的远端会直连失败。
        Assert.Contains("-e use_proxy=yes -e https_proxy='http://proxy.local:8080'", script);
    }
}
