using System.Net;
using System.Text.RegularExpressions;

namespace Dsh.Runtime;

/**
 * 本机出口代理: 本进程所有出站 HTTP 共用的 IWebProxy, 由 settings.yaml 的 network.outbound_proxy 配置, 经 ctx 属性共享。
 * 地址留空时委托给系统代理(HttpClient.DefaultProxy); Configure 原子替换路由, 已建好的 HttpClient 从下一个请求起生效。
 * 回环地址、localhost、不带点的本地主机名与私网/链路本地 IP 字面量始终直连。
 */
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

    private Route _route = Route.System;

    private sealed record Route(Uri? Address, NetworkCredential? Credential, IReadOnlyList<string> Bypass)
    {
        public static readonly Route System = new(null, null, []);
    }

    /** ctx 里没有登记时(未经 ConfigBoot 的测试或工具)返回一份跟随系统代理的独立实例。 */
    public static OutboundProxy FromContext(Context ctx) => ctx.GetProp(PropName) as OutboundProxy ?? new OutboundProxy();

    /** 已配置的代理地址(不含用户名密码); null 表示跟随系统代理。 */
    public Uri? Address => Volatile.Read(ref _route).Address;

    /** 脱敏后的路由描述, 日志、错误与界面只用它。 */
    public string Describe()
    {
        var route = Volatile.Read(ref _route);
        if (route.Address is not { } address)
            return SystemProxyLabel;
        var userInfo = route.Credential is null ? "" : RedactedUserInfo;
        return $"{address.Scheme}://{userInfo}{address.Authority}";
    }

    /** url 留空恢复跟随系统代理; 非法地址抛 ArgumentException(消息已脱敏), 原路由保持不变。 */
    public void Configure(string? url, IReadOnlyList<string>? bypass)
        => Volatile.Write(ref _route, string.IsNullOrWhiteSpace(url) ? Route.System : Parse(url.Trim(), bypass ?? []));

    /** 所有出站 HTTP 的处理器构造点: 代理即本实例, 路由随 Configure 实时变化。 */
    public SocketsHttpHandler CreateHandler(bool allowAutoRedirect = true)
        => new() { Proxy = this, UseProxy = true, AllowAutoRedirect = allowAutoRedirect };

    /** 访问该目标实际经过的代理; 直连返回 null。 */
    public Uri? ProxyFor(Uri destination)
        => IsBypassed(destination) || GetProxy(destination) is not { } proxy || proxy == destination ? null : proxy;

    /** 把 URL 里的用户名密码替换为 ***, 供日志与错误信息使用(非法 URL 同样处理)。 */
    public static string Redact(string url) => UserInfoPattern().Replace(url, RedactedUserInfo);

    public ICredentials? Credentials
    {
        get => this;
        set => throw new NotSupportedException("outbound proxy credentials come from network.outbound_proxy.url");
    }

    public Uri? GetProxy(Uri destination)
    {
        var route = Volatile.Read(ref _route);
        if (route.Address is null)
            return HttpClient.DefaultProxy.GetProxy(destination);
        return Bypassed(route, destination) ? null : route.Address;
    }

    public bool IsBypassed(Uri host)
    {
        var route = Volatile.Read(ref _route);
        return route.Address is null ? HttpClient.DefaultProxy.IsBypassed(host) : Bypassed(route, host);
    }

    public NetworkCredential? GetCredential(Uri uri, string authType)
    {
        var route = Volatile.Read(ref _route);
        return route.Address is null ? HttpClient.DefaultProxy.Credentials?.GetCredential(uri, authType) : route.Credential;
    }

    private static Route Parse(string url, IReadOnlyList<string> bypass)
    {
        var text = url.Contains("://", StringComparison.Ordinal) ? url : $"{DefaultScheme}://{url}";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port < 0)
            throw new ArgumentException($"本机出口代理地址无效(需要 协议://主机:端口): {Redact(url)}");
        if (!SupportedSchemes.Contains(uri.Scheme))
            throw new ArgumentException($"本机出口代理不支持 {uri.Scheme} 协议(支持 http/https/socks4/socks4a/socks5): {Redact(url)}");
        var address = new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri;
        return new Route(address, CredentialOf(uri.UserInfo), [.. bypass.Select(NormalizeBypass).Where(entry => entry.Length > 0)]);
    }

    private static NetworkCredential? CredentialOf(string userInfo)
    {
        if (userInfo.Length == 0)
            return null;
        var colon = userInfo.IndexOf(':');
        return colon < 0
            ? new NetworkCredential(Uri.UnescapeDataString(userInfo), "")
            : new NetworkCredential(Uri.UnescapeDataString(userInfo[..colon]), Uri.UnescapeDataString(userInfo[(colon + 1)..]));
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
