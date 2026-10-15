using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Dsh.Transport;

/**
 * Files and directories that only the current OS user can open.
 * They carry local secrets such as endpoint tokens and SSH askpass passwords, so a file is restricted from the moment it exists instead of being tightened afterwards.
 */
public static class OwnerOnlyFiles
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerAll = OwnerReadWrite | UnixFileMode.UserExecute;
    private const int WriteBufferSize = 4096;

    /** Creates the directory, or tightens an existing one, so that only the owner can list or enter it. */
    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows profile directories already inherit an owner/SYSTEM/Administrators-only ACL.
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, OwnerAll);
        File.SetUnixFileMode(path, OwnerAll);
    }

    /** Writes a new owner-only file, replacing whatever was at the path. */
    public static void Write(string path, string contents)
    {
        File.Delete(path);
        using var stream = OperatingSystem.IsWindows() ? CreateWindows(path) : CreateUnix(path);
        stream.Write(Encoding.UTF8.GetBytes(contents));
    }

    [UnsupportedOSPlatform("windows")]
    private static FileStream CreateUnix(string path)
        => new(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = OwnerReadWrite,
        });

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindows(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("cannot resolve the current Windows user");
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, WriteBufferSize, FileOptions.None, security);
    }
}
