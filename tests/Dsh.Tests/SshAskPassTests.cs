using System.Diagnostics;
using Dsh.Gui.Services;

namespace Dsh.Tests;

public sealed class SshAskPassTests
{
    private const string HostilePassword = "p&whoami|'\"$(id)`id`%PATH% ";
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerAll = OwnerReadWrite | UnixFileMode.UserExecute;

    [Fact]
    public void Create_KeepsPasswordOutOfTheScript()
    {
        using var askPass = SshAskPass.Create(HostilePassword);

        Assert.DoesNotContain("whoami", File.ReadAllText(askPass.ScriptPath));
    }

    [Fact]
    public async Task Script_PrintsPasswordVerbatim()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var askPass = SshAskPass.Create(HostilePassword);
        using var process = Process.Start(new ProcessStartInfo(askPass.ScriptPath, "Password:")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;

        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal($"{HostilePassword}\n", output);
    }

    [Fact]
    public void Create_RestrictsDirectoryAndSecretToOwner()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var askPass = SshAskPass.Create(HostilePassword);

        Assert.Equal(OwnerAll, File.GetUnixFileMode(askPass.Directory));
        var secrets = Directory.GetFiles(askPass.Directory).Where(path => path != askPass.ScriptPath).ToList();
        Assert.NotEmpty(secrets);
        foreach (var secret in secrets)
            Assert.Equal(OwnerReadWrite, File.GetUnixFileMode(secret));
    }

    [Fact]
    public void Dispose_RemovesHelperFiles()
    {
        var askPass = SshAskPass.Create(HostilePassword);

        askPass.Dispose();

        Assert.False(Directory.Exists(askPass.Directory));
    }
}
