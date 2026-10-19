using System.Security.Cryptography;
using System.Text.Json;
using Dsh.Boot;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Host;

internal sealed class HarnessRemoteNetworkBackend(HarnessHome home, OutboundProxy proxy) : IRemoteNetworkBackend
{
    private readonly Lock _gate = new();
    private readonly string _ownerId = Guid.NewGuid().ToString("N");

    public Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(ReadState());
    }

    public Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!string.Equals(request.ExpectedRevision, ReadState().Revision, StringComparison.Ordinal))
                throw new JsonRpcException(NetworkProtocol.RevisionConflict, "outbound proxy settings changed; reload before saving");
            cancellationToken.ThrowIfCancellationRequested();
            HarnessSettings.SaveOutboundProxy(home, ToLocalSettings(request.Settings), proxy);
            return Task.FromResult(ReadState());
        }
    }

    public async Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OutboundProxySettings settings;
        lock (_gate)
            settings = ToLocalSettings(request.Draft ?? ReadSettings());
        if (request.ProbeUrl is { } probeUrl)
            settings.ProbeUrl = probeUrl;
        var result = await OutboundProxyProbe.RunAsync(settings, cancellationToken, "远端服务器出口");
        return new RemoteOutboundProxyProbeResult(result.Reachable, result.Message);
    }

    private RemoteOutboundProxyState ReadState()
    {
        var settings = ReadSettings();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(settings, NetworkProtocolJsonContext.Default.RemoteOutboundProxySettings);
        var revision = Convert.ToHexStringLower(SHA256.HashData(canonical));
        return new RemoteOutboundProxyState(_ownerId, home.Root, revision, settings);
    }

    private RemoteOutboundProxySettings ReadSettings()
    {
        var settings = HarnessSettings.Load(home).Network?.OutboundProxy;
        return new RemoteOutboundProxySettings(settings?.Url, settings?.Bypass?.ToArray() ?? [], settings?.ProbeUrl);
    }

    private static OutboundProxySettings ToLocalSettings(RemoteOutboundProxySettings settings) => new()
    {
        Url = settings.Url,
        Bypass = settings.Bypass?.ToList() ?? [],
        ProbeUrl = settings.ProbeUrl,
    };
}
