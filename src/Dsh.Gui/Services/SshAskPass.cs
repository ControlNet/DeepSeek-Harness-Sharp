using System.Diagnostics;
using System.Text;
using Dsh.Transport;

namespace Dsh.Gui.Services;

/**
 * SSH_ASKPASS helper for password authentication.
 * The script only reads a sibling secret file located relative to itself, so neither the password nor any path text is ever interpolated into shell or cmd code.
 * Both files live in a fresh owner-only temp directory that is removed on dispose.
 */
public sealed class SshAskPass : IDisposable
{
    private const string SecretFileName = "secret";
    private const string UnixScript = "#!/bin/sh\nexec cat -- \"$(dirname -- \"$0\")/secret\"\n";
    private const string WindowsScript = "@echo off\r\ntype \"%~dp0secret\"\r\n";
    private const UnixFileMode OwnerExecutable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private SshAskPass(string directory, string scriptPath)
    {
        Directory = directory;
        ScriptPath = scriptPath;
    }

    public string Directory { get; }

    public string ScriptPath { get; }

    public static SshAskPass Create(string password)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("dsh-askpass-").FullName;
        OwnerOnlyFiles.Write(Path.Combine(directory, SecretFileName), $"{password}\n");
        var scriptPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "askpass.cmd" : "askpass.sh");
        File.WriteAllText(scriptPath, OperatingSystem.IsWindows() ? WindowsScript : UnixScript, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(scriptPath, OwnerExecutable);
        return new SshAskPass(directory, scriptPath);
    }

    /** SSH_ASKPASS_REQUIRE=force makes OpenSSH use the helper even when DISPLAY is unset or a terminal is attached. */
    public void Apply(ProcessStartInfo startInfo)
    {
        startInfo.Environment["SSH_ASKPASS"] = ScriptPath;
        startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        startInfo.Environment["DISPLAY"] = "dsh:0";
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
