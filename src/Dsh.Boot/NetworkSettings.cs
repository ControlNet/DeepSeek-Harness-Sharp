using YamlDotNet.Serialization;

namespace Dsh.Boot;

/** Outbound settings owned by this harness home; SSH transport proxies remain machine connection settings. */
public sealed class NetworkSettings
{
    [YamlMember(Alias = "outbound_proxy")]
    public OutboundProxySettings? OutboundProxy { get; set; }

    [YamlMember(Alias = "borrowed_outbound_machine")]
    public string? BorrowedOutboundMachine { get; set; }
}

/** This process's HTTP egress, shared by its workspaces, tools, model providers, and downloads. */
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
