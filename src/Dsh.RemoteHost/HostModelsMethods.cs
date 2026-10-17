using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

internal static class HostModelsMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(IRemoteModelsBackend backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case ModelsProtocol.MethodReasoningEfforts:
            {
                var request = Require(message, ModelsProtocolJsonContext.Default.RemoteModelRef, "reasoningEfforts requires provider and model");
                return Serialize(await backend.ListReasoningEffortsAsync(request.Provider, request.Model, cancellationToken).ConfigureAwait(false), ModelsProtocolJsonContext.Default.IReadOnlyListString);
            }
            case ModelsProtocol.MethodProviderModels:
                return Serialize(await backend.ListProviderModelsAsync(Require(message, ModelsProtocolJsonContext.Default.RemoteProviderRef, "models requires provider").Provider, cancellationToken).ConfigureAwait(false), ModelsProtocolJsonContext.Default.IReadOnlyListString);
            default:
                throw MethodNotFound(name);
        }
    }
}
