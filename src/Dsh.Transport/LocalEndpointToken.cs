using System.Security.Cryptography;
using System.Text;

namespace Dsh.Transport;

/**
 * Per-start secret that proves a client of a local endpoint (Unix socket or loopback TCP port) runs as the same OS user.
 * The server writes it to an owner-only file next to the endpoint file before the endpoint becomes connectable, and clients read it from there.
 * On Windows this is the only barrier for loopback TCP; on Unix it backs up the owner-only socket and directory.
 */
public sealed class LocalEndpointToken : IDisposable
{
    private const int TokenBytes = 32;
    private const string TokenExtension = ".token";

    private readonly string _path;
    private readonly byte[] _value;

    private LocalEndpointToken(string path, string value)
    {
        _path = path;
        _value = Encoding.UTF8.GetBytes(value);
        Value = value;
    }

    public string Value { get; }

    /** The token file shares the endpoint file's name with a .token extension, e.g. run/dsh.sock and run/dsh.port both map to run/dsh.token. */
    public static string PathFor(string endpointPath) => Path.ChangeExtension(endpointPath, TokenExtension);

    public static LocalEndpointToken Issue(string endpointPath)
    {
        var path = PathFor(endpointPath);
        OwnerOnlyFiles.EnsureDirectory(Path.GetDirectoryName(path)!);
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes));
        OwnerOnlyFiles.Write(path, value);
        return new LocalEndpointToken(path, value);
    }

    /** Returns null when no server has issued a token for this endpoint. */
    public static string? Read(string endpointPath)
    {
        try
        {
            return File.ReadAllText(PathFor(endpointPath)).Trim();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public bool Matches(string? presented)
        => presented is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _value);

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
