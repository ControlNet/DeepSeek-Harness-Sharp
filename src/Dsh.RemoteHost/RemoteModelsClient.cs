using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Models domain. */
public sealed class RemoteModelsClient : IRemoteModels
{
    private readonly RemoteHostClient _host;

    internal RemoteModelsClient(RemoteHostClient host) => _host = host;

    public Task<IReadOnlyList<string>> ListReasoningEffortsAsync(string provider, string model, CancellationToken cancellationToken = default)
        => _host.SendAsync(ModelsProtocol.MethodReasoningEfforts, Serialize(new RemoteModelRef(provider, model), ModelsProtocolJsonContext.Default.RemoteModelRef), ModelsProtocolJsonContext.Default.IReadOnlyListString, cancellationToken);

    public Task<IReadOnlyList<string>> ListProviderModelsAsync(string provider, CancellationToken cancellationToken = default)
        => _host.SendAsync(ModelsProtocol.MethodProviderModels, Serialize(new RemoteProviderRef(provider), ModelsProtocolJsonContext.Default.RemoteProviderRef), ModelsProtocolJsonContext.Default.IReadOnlyListString, cancellationToken);
}
