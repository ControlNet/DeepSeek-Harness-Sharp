using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dsh.Transport;

namespace Dsh.WebBridge;

/**
 * Browser-side access control of the bridge.
 * The per-start web access token is exchanged once for a random session id in an HttpOnly, SameSite=Strict cookie; the token itself never lands in the cookie.
 * The Host header must name a loopback address (DNS rebinding), and state-changing or upgrading requests must carry a same-origin Origin header (cross-site requests).
 */
internal sealed class WebAccessGate(LocalEndpointToken token)
{
    public const string CookieName = "dsh_web_session";

    private const int SessionIdBytes = 32;

    /** Logins beyond this many evict the oldest session, so repeated logins cannot grow memory without bound. */
    private const int MaxSessions = 32;

    private const string TokenField = "token=";

    private static readonly string[] LoopbackHostNames = ["127.0.0.1", "localhost", "[::1]"];

    private readonly List<byte[]> _sessions = [];

    /** Accepts `name` or `name:port` where name is a loopback literal; a forwarded local port (ssh -L) may differ from the bridge's own port. */
    public static bool IsAllowedHost(string? host)
        => host is not null && LoopbackHostNames.Any(name => host.Equals(name, StringComparison.OrdinalIgnoreCase)
            || (host.StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase) && IsPort(host.AsSpan(name.Length + 1))));

    /** The page that issued the request must have been served by this same origin (the Host header was validated before). */
    public static bool IsSameOrigin(string? origin, string host)
        => origin is not null && (origin.Equals($"http://{host}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"https://{host}", StringComparison.OrdinalIgnoreCase));

    /** Checks the form-encoded login body in constant time; returns the Set-Cookie value of a fresh session, or null when the token is wrong. */
    public string? Login(byte[] formBody)
    {
        if (!token.Matches(TokenFromForm(Encoding.UTF8.GetString(formBody))))
            return null;
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(SessionIdBytes));
        lock (_sessions)
        {
            _sessions.Add(Encoding.ASCII.GetBytes(sessionId));
            if (_sessions.Count > MaxSessions)
                _sessions.RemoveAt(0);
        }

        return $"{CookieName}={sessionId}; Path=/; HttpOnly; SameSite=Strict";
    }

    public bool HasSession(string? cookieHeader)
    {
        if (cookieHeader is null)
            return false;
        foreach (var pair in cookieHeader.Split(';', StringSplitOptions.TrimEntries))
        {
            if (pair.StartsWith($"{CookieName}=", StringComparison.Ordinal) && IsSession(pair[(CookieName.Length + 1)..]))
                return true;
        }

        return false;
    }

    private bool IsSession(string presented)
    {
        var bytes = Encoding.ASCII.GetBytes(presented);
        lock (_sessions)
            return _sessions.Any(session => CryptographicOperations.FixedTimeEquals(session, bytes));
    }

    private static bool IsPort(ReadOnlySpan<char> text)
        => ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /** Form encoding turns spaces into '+'; surrounding whitespace of a pasted token is dropped. */
    private static string? TokenFromForm(string form)
    {
        var field = form.Split('&').FirstOrDefault(pair => pair.StartsWith(TokenField, StringComparison.Ordinal));
        return field is null ? null : Uri.UnescapeDataString(field[TokenField.Length..].Replace('+', ' ')).Trim();
    }
}
