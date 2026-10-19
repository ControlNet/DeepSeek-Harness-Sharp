namespace Dsh.RemoteHost;

public sealed record RemoteOutboundProxySettings(string? Url = null, IReadOnlyList<string>? Bypass = null, string? ProbeUrl = null);

public sealed record RemoteOutboundProxyState(string OwnerId, string HostHome, string Revision, RemoteOutboundProxySettings Settings);

public sealed record RemoteOutboundProxySetRequest(string ExpectedRevision, RemoteOutboundProxySettings Settings);

public sealed record RemoteOutboundProxyProbeRequest(RemoteOutboundProxySettings? Draft = null, string? ProbeUrl = null);

public readonly record struct RemoteOutboundProxyProbeResult(bool Reachable, string Message);
