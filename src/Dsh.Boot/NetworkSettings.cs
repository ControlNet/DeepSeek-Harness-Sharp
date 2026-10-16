using YamlDotNet.Serialization;

namespace Dsh.Boot;

/** settings.yaml 的 network 段: 只描述本机自身的网络出口; 每台远程机器自己的代理随它的机器条目配置。 */
public sealed class NetworkSettings
{
    [YamlMember(Alias = "outbound_proxy")]
    public OutboundProxySettings? OutboundProxy { get; set; }
}

/** 本机出口代理: 本机发出的出站 HTTP(web_search/web_fetch/LLM/下载远端 dsharp 等)经它访问外网。 */
public sealed class OutboundProxySettings
{
    public const string DefaultProbeUrl = "https://www.gstatic.com/generate_204";

    /** http://、https://、socks4://、socks4a://、socks5:// 代理 URL, 可带 user:pass@; 留空跟随系统代理。 */
    [YamlMember(Alias = "url")]
    public string? Url { get; set; }

    /** 不走代理的主机(含其子域), 只在 url 非空时生效; 回环地址、不带点的主机名与私网 IP 始终直连。 */
    [YamlMember(Alias = "bypass")]
    public List<string> Bypass { get; set; } = [];

    /** 连通性自检的探测地址; 留空用 https://www.gstatic.com/generate_204。 */
    [YamlMember(Alias = "probe_url")]
    public string? ProbeUrl { get; set; }
}
