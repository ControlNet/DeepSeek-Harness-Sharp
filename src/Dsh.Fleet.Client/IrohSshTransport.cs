using System.Text;

namespace Dsh.Fleet;

/** The iroh-specific SSH proxy boundary shared by command execution, deployment and Host connections. */
public static class IrohSshTransport
{
    public const int EndpointIdLength = 64;

    public static void Validate(MachineSpec machine)
    {
        ValidateRelayUrls(machine.RelayUrls ?? []);
        if (!machine.IsIroh)
            return;
        if (machine.EndpointId is not { Length: EndpointIdLength } endpoint || endpoint.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidOperationException($"iroh machine \"{machine.Name}\" requires a {EndpointIdLength}-character hexadecimal Endpoint ID");
        if (!string.IsNullOrEmpty(machine.Proxy) || (machine.SshOptions ?? []).Any(IsProxyOverride))
            throw new InvalidOperationException("iroh machines use the bundled tunnel; remove ProxyCommand, ProxyJump and Proxy settings");
    }

    internal static void ValidateRelayUrls(IReadOnlyList<string> relayUrls)
    {
        foreach (var relay in relayUrls)
            if (!Uri.TryCreate(relay, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http") || uri.Host.Length == 0
                || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidOperationException("iroh relay URLs must be absolute HTTP(S) addresses without credentials or fragments");
    }

    public static string ProxyCommand(MachineSpec machine)
    {
        Validate(machine);
        var command = new StringBuilder(QuoteArgument(IrohSshComponent.RequireExecutable()));
        command.Append($" proxy {machine.EndpointId!.ToLowerInvariant()}");
        foreach (var relay in machine.RelayUrls ?? [])
            command.Append($" --relay-url {QuoteArgument(new Uri(relay).AbsoluteUri)}");
        return command.ToString();
    }

    internal static bool IsProxyOverride(string option)
    {
        var name = option.Split(['=', ' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(name, "ProxyCommand", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ProxyJump", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteArgument(string value)
    {
        var literal = value.Replace("%", "%%", StringComparison.Ordinal);
        return OperatingSystem.IsWindows()
            ? $"\"{literal}\""
            : RemoteShell.Quote(literal);
    }
}
