namespace Dsh.Fleet;

/** How the client reaches a machine's host. */
public static class MachineKinds
{
    /** The host on this computer, reached through its loopback endpoint (host.sock). */
    public const string Local = "local";

    /** A remote host reached through `ssh -T ... dsharp host --serve --stdio`. */
    public const string Ssh = "ssh";
}

/** SSH authentication modes. */
public static class SshAuth
{
    public const string Password = "password";
    public const string Key = "key";
    public const string Agent = "agent";
}

/**
 * One machine of the fleet.
 * Local machines use only Name and HarnessHome; SSH machines use the connection fields as well.
 * HarnessHome is the harness home the host runs under (DSH_HOME); empty means the default resolution on that machine (DSH_HOME or ~/.dsh, following .storage-root).
 * RemotePath is the remote directory that holds ./dsharp and is the default working directory of new remote sessions.
 * SshOptions are extra `-o Key=Value` options placed before the built-in ones, so they take precedence (ssh keeps the first value it sees).
 */
public sealed record MachineSpec(
    string Name,
    string Kind,
    string Host = "",
    int Port = MachineSpec.DefaultSshPort,
    string User = "",
    string Auth = SshAuth.Agent,
    string? KeyPath = null,
    string? Proxy = null,
    string? RemotePath = null,
    string? Password = null,
    string? HarnessHome = null,
    IReadOnlyList<string>? SshOptions = null)
{
    public const int DefaultSshPort = 22;

    /** The implicit machine every client has: the host on this computer. */
    public const string LocalName = "local";

    public static MachineSpec Local(string? harnessHome = null, string name = LocalName)
        => new(name, MachineKinds.Local, HarnessHome: harnessHome);

    public static MachineSpec Ssh(string name, string host, int port, string user, string auth)
        => new(name, MachineKinds.Ssh, host, port, user, auth);

    public bool IsLocal => Kind == MachineKinds.Local;

    /** user@host:port for SSH machines, the harness home (or "this computer") for local ones. */
    public string Target => IsLocal ? HarnessHome ?? "this computer" : $"{User}@{Host}:{Port}";
}
