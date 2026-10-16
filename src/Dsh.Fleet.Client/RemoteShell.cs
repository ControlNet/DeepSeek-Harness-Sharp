namespace Dsh.Fleet;

/**
 * POSIX shell fragments shared by deployment and diagnostics on a machine.
 * The harness home is resolved on the machine itself with the same rule as Dsh.Transport.LocalEndpointRoot: $DSH_HOME or ~/.dsh, redirected by a non-empty .storage-root marker.
 */
public static class RemoteShell
{
    /** Same variable as Dsh.Boot.HarnessHome.HomeEnv; it is how a configured harness home reaches the remote dsharp. */
    public const string HomeVariable = "DSH_HOME";

    /** Sets $h to the resolved harness home. */
    public const string ResolveHome =
        """h="${DSH_HOME:-$HOME/.dsh}"; if [ -s "$h/.storage-root" ]; then r=$(tr -d '\r\n' < "$h/.storage-root"); [ -n "$r" ] && h="$r"; fi; """;

    /**
     * Non-interactive ssh sessions often lack XDG_RUNTIME_DIR, so `systemctl --user` cannot reach the user manager and the machine looks like it has no systemd.
     * Point it at the standard runtime directory when that exists.
     */
    public const string UserManagerEnvironment =
        """if [ -z "$XDG_RUNTIME_DIR" ] && [ -d "/run/user/$(id -u)" ]; then export XDG_RUNTIME_DIR="/run/user/$(id -u)"; fi; """;

    /** The systemd user unit that `dsharp host install-service` writes (HostAutostart.UnitName). */
    public const string ServiceUnit = "dsharp-host.service";

    /** Sets $u to the path of that unit. */
    public const string ResolveUnit = $$"""u="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/{{ServiceUnit}}"; """;

    /** `DSH_HOME=<home> ` to prefix a single command, or empty when the machine uses the default home. */
    public static string HomeAssignment(string? harnessHome)
        => string.IsNullOrWhiteSpace(harnessHome) ? "" : $"{HomeVariable}={Quote(harnessHome)} ";

    /** The common start of every script run on a machine: the configured harness home and the user manager environment. */
    public static string Prelude(MachineSpec machine)
    {
        var home = string.IsNullOrWhiteSpace(machine.HarnessHome) ? "" : $"export {HomeVariable}={Quote(machine.HarnessHome)}; ";
        return $"{home}{UserManagerEnvironment}";
    }

    /** POSIX single quoting; a leading `~` or `~/` stays expandable as "$HOME". */
    public static string Quote(string value)
    {
        if (value == "~")
            return "\"$HOME\"";
        if (value.StartsWith("~/", StringComparison.Ordinal))
            return $"\"$HOME\"{Quote(value[1..])}";
        return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }
}
