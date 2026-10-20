using System.Net;
using System.Text.RegularExpressions;

namespace Dsh.Runtime;

/// <summary>
/// Process-local outbound routing.
/// Configuration changes retire the previous generation; handlers reuse connection pools while a route is unchanged.
/// </summary>
public sealed partial class OutboundProxy : IWebProxy, ICredentials
{
    public const string PropName = "outboundProxy";

    private const string DefaultScheme = "http";
    private const string RedactedUserInfo = "***@";
    private const string MatchAll = "*";
    private const string SystemProxyLabel = "系统代理";

    private static readonly HashSet<string> SupportedSchemes = new(["http", "https", "socks4", "socks4a", "socks5"], StringComparer.OrdinalIgnoreCase);

    private static readonly IPNetwork[] LocalNetworks =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
    ];

    private readonly object _gate = new();
    private RouteGeneration _route = new(0, new Route(null, null, []));

    internal sealed record Route(Uri? Address, CredentialHolder? Credential, string[] Bypass)
    {
        internal bool EquivalentTo(Route other)
            => Address == other.Address
                && CredentialHolder.Equivalent(Credential, other.Credential)
                && Bypass.AsSpan().SequenceEqual(other.Bypass);
    }

    internal sealed class RouteGeneration
    {
        internal RouteGeneration(long number, Route configuration)
        {
            Number = number;
            Configuration = configuration;
            if (configuration.Credential is { } credential)
                Lifetime.Register(credential);
        }

        internal long Number { get; }
        internal Route Configuration { get; }
        internal OutboundResourceLifetime Lifetime { get; } = new();
    }

    /** ctx 里没有登记时(未经 ConfigBoot 的测试或工具)返回一份跟随系统代理的独立实例。 */
    public static OutboundProxy FromContext(Context ctx) => ctx.GetProp(PropName) as OutboundProxy ?? new OutboundProxy();

    /** 已配置的代理地址(不含用户名密码); null 表示跟随系统代理。 */
    public Uri? Address => Volatile.Read(ref _route).Configuration.Address;

    /** 脱敏后的路由描述, 日志、错误与界面只用它。 */
    public string Describe()
    {
        var route = Volatile.Read(ref _route).Configuration;
        if (route.Address is not { } address)
            return SystemProxyLabel;
        var userInfo = route.Credential is null ? "" : RedactedUserInfo;
        return $"{address.Scheme}://{userInfo}{address.Authority}";
    }

    /// <summary>Parses before publishing; invalid or equivalent settings leave active requests alone.</summary>
    public void Configure(string? url, IReadOnlyList<string>? bypass)
    {
        var configuration = string.IsNullOrWhiteSpace(url)
            ? new Route(null, null, [])
            : Parse(url.Trim(), bypass ?? []);
        RouteGeneration previous;
        IDisposable[] retired;
        lock (_gate)
        {
            previous = _route;
            if (previous.Configuration.EquivalentTo(configuration))
            {
                configuration.Credential?.Dispose();
                return;
            }
            var next = new RouteGeneration(previous.Number + 1, configuration);
            retired = previous.Lifetime.BeginRetirement();
            Volatile.Write(ref _route, next);
        }
        previous.Lifetime.CompleteRetirement(retired);
    }

    /// <summary>Creates a handler whose sends capture a generation and reuse its connection pools.</summary>
    public HttpMessageHandler CreateHandler(
        bool allowAutoRedirect = true,
        DecompressionMethods automaticDecompression = DecompressionMethods.None)
        => new OutboundHttpHandler(this, allowAutoRedirect, automaticDecompression);

    /// <summary>Captures routing and credentials before asynchronous destination validation begins.</summary>
    public RouteSnapshot CaptureRoute(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var generation = Volatile.Read(ref _route);
        generation.Lifetime.ThrowIfRetired();
        var route = generation.Configuration;
        var systemProxy = route.Address is null ? HttpClient.DefaultProxy : null;
        var proxy = EffectiveProxy(route, systemProxy, destination);
        var credentials = route.Credential;
        if (systemProxy is not null && proxy is not null && systemProxy.Credentials is { } source)
        {
            credentials = CredentialHolder.Capture(source, proxy);
            generation.Lifetime.Register(credentials);
        }
        generation.Lifetime.ThrowIfRetired();
        return new RouteSnapshot(generation, proxy, credentials, systemProxy);
    }

    /// <summary>Returns the effective proxy endpoint, or null for a direct route.</summary>
    public Uri? ProxyFor(Uri destination)
    {
        var route = Volatile.Read(ref _route).Configuration;
        return EffectiveProxy(route, route.Address is null ? HttpClient.DefaultProxy : null, destination);
    }

    private static Uri? EffectiveProxy(Route route, IWebProxy? systemProxy, Uri destination)
    {
        if (systemProxy is null)
            return Bypassed(route, destination) ? null : route.Address;
        if (systemProxy.IsBypassed(destination))
            return null;
        var proxy = systemProxy.GetProxy(destination);
        return proxy == destination ? null : proxy;
    }

    /// <summary>An immutable route capture; its token and factories share generation ownership.</summary>
    public readonly struct RouteSnapshot
    {
        private readonly RouteGeneration _generation;
        private readonly CredentialHolder? _credentials;
        private readonly IWebProxy? _systemProxy;

        internal RouteSnapshot(RouteGeneration generation, Uri? proxy, CredentialHolder? credentials, IWebProxy? systemProxy)
        {
            _generation = generation;
            ProxyUri = proxy;
            _credentials = credentials;
            _systemProxy = systemProxy;
        }

        public long Generation => _generation.Number;
        public Uri? ProxyUri { get; }
        public CancellationToken CancellationToken => _generation.Lifetime.Token;

        public SocketsHttpHandler CreateHandler(bool allowAutoRedirect)
            => CreateHandlerCore(allowAutoRedirect, DecompressionMethods.None, null, null, false);

        public SocketsHttpHandler CreateHandler(
            bool allowAutoRedirect,
            Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> connectCallback)
        {
            ArgumentNullException.ThrowIfNull(connectCallback);
            return CreateHandlerCore(allowAutoRedirect, DecompressionMethods.None, connectCallback, null, false);
        }

        internal void ThrowIfRetired() => _generation.Lifetime.ThrowIfRetired();

        internal void Register(IDisposable resource) => _generation.Lifetime.Register(resource);

        internal void Unregister(IDisposable resource) => _generation.Lifetime.Unregister(resource);

        internal SocketsHttpHandler CreatePooledHandler(
            bool allowAutoRedirect, DecompressionMethods decompression,
            OutboundResourceLifetime connections, CookieContainer cookies)
            => CreateHandlerCore(allowAutoRedirect, decompression, null, connections, true, cookies);

        private SocketsHttpHandler CreateHandlerCore(
            bool allowAutoRedirect,
            DecompressionMethods decompression,
            Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connector,
            OutboundResourceLifetime? connections,
            bool routeRedirects,
            CookieContainer? cookies = null)
        {
            ThrowIfRetired();
            var lifetime = _generation.Lifetime;
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = allowAutoRedirect,
                AutomaticDecompression = decompression,
                UseProxy = routeRedirects || ProxyUri is not null,
                Proxy = routeRedirects
                    ? new CapturedProxy(_generation, _systemProxy, ProxyUri, _credentials)
                    : ProxyUri is { } endpoint ? new CapturedProxy(endpoint, _credentials) : null,
                ConnectCallback = (context, token) => OutboundConnectionStream.ConnectAsync(
                    context, token, lifetime, connections, connector),
            };
            if (cookies is not null)
                handler.CookieContainer = cookies;
            if (connections is null)
                lifetime.Register(handler);
            else
            {
                try
                {
                    lifetime.ThrowIfRetired();
                    connections.Register(handler);
                }
                catch
                {
                    handler.Dispose();
                    throw;
                }
            }
            return handler;
        }
    }

    private sealed class CapturedProxy : IWebProxy, ICredentials
    {
        private readonly Route? _route;
        private readonly IWebProxy? _systemProxy;
        private readonly Uri? _endpoint;
        private readonly CredentialHolder? _credentials;
        private readonly OutboundResourceLifetime? _lifetime;
        private readonly Dictionary<Uri, CredentialHolder?> _systemCredentials = [];

        internal CapturedProxy(Uri endpoint, CredentialHolder? credentials)
        {
            _endpoint = endpoint;
            _credentials = credentials;
        }

        internal CapturedProxy(RouteGeneration generation, IWebProxy? systemProxy, Uri? endpoint, CredentialHolder? credentials)
        {
            _route = generation.Configuration;
            _lifetime = generation.Lifetime;
            _systemProxy = systemProxy;
            _credentials = credentials;
            if (systemProxy is not null && endpoint is not null)
                _systemCredentials.Add(endpoint, credentials);
        }

        public ICredentials? Credentials
        {
            get => _systemProxy is null ? _credentials : this;
            set => throw new NotSupportedException("Captured proxy credentials are immutable.");
        }

        public Uri? GetProxy(Uri destination)
        {
            var endpoint = _route is null ? _endpoint : EffectiveProxy(_route, _systemProxy, destination);
            if (_systemProxy is not null && endpoint is not null)
            {
                // Redirects can select another system proxy.
                // Capture that endpoint's credentials separately rather than sending the first proxy's secrets.
                lock (_systemCredentials)
                {
                    if (!_systemCredentials.ContainsKey(endpoint))
                    {
                        CredentialHolder? credentials = null;
                        if (_systemProxy.Credentials is { } source)
                        {
                            credentials = CredentialHolder.Capture(source, endpoint);
                            _lifetime!.Register(credentials);
                        }
                        _systemCredentials.Add(endpoint, credentials);
                    }
                }
            }
            return endpoint;
        }

        public bool IsBypassed(Uri host) => GetProxy(host) is null;

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            lock (_systemCredentials)
                return _systemCredentials.TryGetValue(uri, out var credentials)
                    ? credentials?.GetCredential(uri, authType) : null;
        }
    }

    internal sealed class CredentialHolder : ICredentials, IDisposable
    {
        private static readonly string[] AuthenticationTypes = ["Basic", "Digest", "NTLM", "Negotiate", "Kerberos"];
        private NetworkCredential? _credential;
        private Dictionary<string, NetworkCredential>? _systemCredentials;
        private readonly Uri? _systemEndpoint;

        internal CredentialHolder(NetworkCredential credential) => _credential = credential;

        private CredentialHolder(Uri endpoint, Dictionary<string, NetworkCredential> credentials)
        {
            _systemEndpoint = endpoint;
            _systemCredentials = credentials;
        }

        internal static CredentialHolder Capture(ICredentials source, Uri endpoint)
        {
            if (source is NetworkCredential networkCredential)
                return new CredentialHolder(Copy(networkCredential));
            var credentials = new Dictionary<string, NetworkCredential>(StringComparer.OrdinalIgnoreCase);
            if (endpoint.Scheme is "socks4" or "socks4a" or "socks5")
            {
                if (source.GetCredential(endpoint, endpoint.Scheme) is { } credential)
                    credentials.Add(endpoint.Scheme, Copy(credential));
            }
            else
                foreach (var type in AuthenticationTypes)
                    if (source.GetCredential(endpoint, type) is { } credential)
                        credentials.Add(type, Copy(credential));
            return new CredentialHolder(endpoint, credentials);
        }

        internal static bool Equivalent(CredentialHolder? left, CredentialHolder? right)
        {
            var first = left?._credential;
            var second = right?._credential;
            return first is null ? second is null
                : second is not null && first.UserName == second.UserName
                    && first.Password == second.Password && first.Domain == second.Domain;
        }

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if (Volatile.Read(ref _credential) is { } credential)
                return Copy(credential);
            return uri == _systemEndpoint
                && Volatile.Read(ref _systemCredentials) is { } credentials
                && credentials.TryGetValue(authType, out var systemCredential) ? Copy(systemCredential) : null;
        }

        private static NetworkCredential Copy(NetworkCredential credential)
            => ReferenceEquals(credential, CredentialCache.DefaultNetworkCredentials)
                ? credential : new NetworkCredential(credential.UserName, credential.Password, credential.Domain);

        public void Dispose()
        {
            Interlocked.Exchange(ref _credential, null);
            Interlocked.Exchange(ref _systemCredentials, null);
        }
    }

    /** 把 URL 里的用户名密码替换为 ***, 供日志与错误信息使用(非法 URL 同样处理)。 */
    public static string Redact(string url) => UserInfoPattern().Replace(url, RedactedUserInfo);

    public ICredentials? Credentials
    {
        get => this;
        set => throw new NotSupportedException("outbound proxy credentials come from network.outbound_proxy.url");
    }

    public Uri? GetProxy(Uri destination)
    {
        var route = Volatile.Read(ref _route).Configuration;
        if (route.Address is null)
            return HttpClient.DefaultProxy.GetProxy(destination);
        return Bypassed(route, destination) ? null : route.Address;
    }

    public bool IsBypassed(Uri host)
    {
        var route = Volatile.Read(ref _route).Configuration;
        return route.Address is null ? HttpClient.DefaultProxy.IsBypassed(host) : Bypassed(route, host);
    }

    public NetworkCredential? GetCredential(Uri uri, string authType)
    {
        var route = Volatile.Read(ref _route).Configuration;
        return route.Address is null ? HttpClient.DefaultProxy.Credentials?.GetCredential(uri, authType) : route.Credential?.GetCredential(uri, authType);
    }

    private static Route Parse(string url, IReadOnlyList<string> bypass)
    {
        var text = url.Contains("://", StringComparison.Ordinal) ? url : $"{DefaultScheme}://{url}";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port < 0)
            throw new ArgumentException($"本机出口代理地址无效(需要 协议://主机:端口): {Redact(url)}");
        if (!SupportedSchemes.Contains(uri.Scheme))
            throw new ArgumentException($"本机出口代理不支持 {uri.Scheme} 协议(支持 http/https/socks4/socks4a/socks5): {Redact(url)}");
        var address = new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri;
        var normalized = bypass.Select(NormalizeBypass).Where(entry => entry.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Contains(MatchAll))
            normalized = [MatchAll];
        else
            normalized = normalized.Where(entry => !normalized.Any(other =>
                entry.Length > other.Length && entry.EndsWith(other, StringComparison.Ordinal)
                    && entry[entry.Length - other.Length - 1] == '.')).ToArray();
        return new Route(address, CredentialOf(uri.UserInfo), normalized);
    }

    private static CredentialHolder? CredentialOf(string userInfo)
    {
        if (userInfo.Length == 0)
            return null;
        var colon = userInfo.IndexOf(':');
        return new CredentialHolder(colon < 0
            ? new NetworkCredential(Uri.UnescapeDataString(userInfo), "")
            : new NetworkCredential(Uri.UnescapeDataString(userInfo[..colon]), Uri.UnescapeDataString(userInfo[(colon + 1)..])));
    }

    /** "example.com"、".example.com"、"*.example.com" 都表示该域名及其子域; 单独的 "*" 表示全部直连。 */
    private static string NormalizeBypass(string entry)
    {
        var trimmed = entry.Trim().ToLowerInvariant();
        return trimmed == MatchAll ? trimmed : trimmed.TrimStart('*').TrimStart('.');
    }

    private static bool Bypassed(Route route, Uri destination)
    {
        var host = destination.IdnHost.Trim('[', ']').ToLowerInvariant();
        var local = IPAddress.TryParse(host, out var address)
            ? LocalNetworks.Any(network => network.Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))
            : !host.Contains('.');
        if (destination.IsLoopback || local)
            return true;
        return route.Bypass.Any(entry => entry == MatchAll || host == entry || host.EndsWith($".{entry}", StringComparison.Ordinal));
    }

    [GeneratedRegex("""(?<=^|://)[^/@\s]*@""")]
    private static partial Regex UserInfoPattern();
}
