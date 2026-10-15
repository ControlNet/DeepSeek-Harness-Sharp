using System.Text.RegularExpressions;
using Dsh.Fleet;

namespace Dsh.WebBridge;

/**
 * Removes credentials from machine text shown to browsers, such as ssh's stderr in a machine's last failure or a WebSocket's close reason.
 * A ProxyCommand's own errors echo its arguments (nc printed "user:password@host" into the failure detail), so URL-style user:password@ parts are masked besides the stored password and proxy command.
 */
internal static partial class CredentialRedaction
{
    private const string Mask = "***";

    public static FleetMachineStatus Redact(FleetMachineStatus status, MachineSpec machine)
        => status.Detail is { Length: > 0 } detail ? status with { Detail = Redact(detail, machine) } : status;

    public static string Redact(string text, MachineSpec machine)
    {
        foreach (var secret in new[] { machine.Password, machine.Proxy })
        {
            if (secret is { Length: > 0 })
                text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return UserInfo().Replace(text, $"{Mask}@");
    }

    [GeneratedRegex("""[^\s/@"':]+:[^\s/@"']+@""")]
    private static partial Regex UserInfo();
}
