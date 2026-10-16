using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dsh.Runtime;

namespace Dsh.Boot;

/** 自检结论: Reachable 为探测地址是否经该出口访问成功, Message 是给用户看的分层原因(已脱敏)。 */
public readonly record struct OutboundProxyProbeResult(bool Reachable, string Message);

/**
 * 本机出口代理连通性自检, GUI 设置页与 TUI /proxy test 共用。
 * 分两层判断: 先直连代理端口(不通即"本机出口代理不通"), 再经代理请求探测地址(任何 HTTP 响应都算出口可用, 407 视为认证失败)。
 */
public static class OutboundProxyProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public static async Task<OutboundProxyProbeResult> RunAsync(OutboundProxySettings? settings, CancellationToken cancellationToken)
    {
        var proxy = new OutboundProxy();
        try
        {
            proxy.Configure(settings?.Url, settings?.Bypass);
        }
        catch (ArgumentException error)
        {
            return new OutboundProxyProbeResult(false, $"本机出口代理不通: 配置无效({error.Message})");
        }
        var probeText = string.IsNullOrWhiteSpace(settings?.ProbeUrl) ? OutboundProxySettings.DefaultProbeUrl : settings.ProbeUrl.Trim();
        if (!Uri.TryCreate(probeText, UriKind.Absolute, out var probe) || probe.Scheme is not ("http" or "https"))
            return new OutboundProxyProbeResult(false, $"探测地址无效(需要 http/https URL): {OutboundProxy.Redact(probeText)}");
        var endpoint = proxy.ProxyFor(probe);
        var layer = proxy.Address is null ? "本机出口(未配置本机出口代理, 跟随系统代理)"
            : endpoint is null ? "本机出口(探测地址在直连名单内)"
            : "本机出口代理";
        var via = endpoint is null ? "直连" : proxy.Address is null ? OutboundProxy.Redact(endpoint.GetLeftPart(UriPartial.Authority)) : proxy.Describe();
        if (endpoint is not null && await ConnectFailureAsync(endpoint, cancellationToken) is { } connectError)
            return new OutboundProxyProbeResult(false, $"{layer}不通: 无法连接代理 {via}({connectError})");
        return await RequestAsync(proxy, probe, layer, via, cancellationToken);
    }

    private static async Task<string?> ConnectFailureAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"{ConnectTimeout.TotalSeconds:0} 秒内未连上";
        }
        catch (SocketException error)
        {
            return error.Message;
        }
    }

    private static async Task<OutboundProxyProbeResult> RequestAsync(OutboundProxy proxy, Uri probe, string layer, string via, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(proxy.CreateHandler(allowAutoRedirect: false)) { Timeout = RequestTimeout };
        var watch = Stopwatch.StartNew();
        HttpStatusCode status;
        try
        {
            using var response = await client.GetAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            status = response.StatusCode;
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.ProxyAuthenticationRequired)
        {
            status = HttpStatusCode.ProxyAuthenticationRequired;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var reason = error is TaskCanceledException ? $"{RequestTimeout.TotalSeconds:0} 秒内无响应" : OutboundProxy.Redact(Flatten(error));
            return new OutboundProxyProbeResult(false, $"{layer}不通: 经 {via} 访问 {probe} 失败({reason})");
        }
        return status == HttpStatusCode.ProxyAuthenticationRequired
            ? new OutboundProxyProbeResult(false, $"{layer}不通: 代理 {via} 要求认证或用户名密码错误(HTTP 407)")
            : new OutboundProxyProbeResult(true, $"{layer}可用: 经 {via} 访问 {probe} 返回 HTTP {(int)status}, 耗时 {watch.ElapsedMilliseconds} ms");
    }

    private static string Flatten(Exception error)
        => error.InnerException is { } inner && inner.Message != error.Message ? $"{error.Message}: {inner.Message}" : error.Message;
}
