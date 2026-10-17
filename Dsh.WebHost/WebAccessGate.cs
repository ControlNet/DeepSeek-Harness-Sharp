using System.Globalization;

namespace Dsh.WebBridge;

/** Loopback Host and same-origin checks remain independent of cookie authentication: they prevent DNS rebinding and cross-site WebSocket/login requests. */
internal static class WebAccessGate
{
    public const string CookieName = "dsh_web_session";
    private static readonly string[] LoopbackHostNames = ["127.0.0.1", "localhost", "[::1]"];

    public static bool IsAllowedHost(string? host)
        => host is not null && LoopbackHostNames.Any(name => host.Equals(name, StringComparison.OrdinalIgnoreCase)
            || (host.StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase)
                && ushort.TryParse(host.AsSpan(name.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _)));

    public static bool IsSameOrigin(string? origin, string host)
        => origin is not null && (origin.Equals($"http://{host}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"https://{host}", StringComparison.OrdinalIgnoreCase));
}
