using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** host.agent.* requests, forwarded to the backend that owns the agent processes. */
internal static class HostAgentMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(IRemoteHostBackend backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case HostProtocol.MethodAgentCatalog:
                return Serialize(await backend.AgentCatalogAsync(cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.IReadOnlyListRemoteAgentCatalogEntry);
            case HostProtocol.MethodAgentStart:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteAgentStartRequest, "agent start requires agentKind and projectPath");
                return Serialize(await backend.StartAgentAsync(request, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteHostSession);
            }
            case HostProtocol.MethodAgentPrompt:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteAgentPrompt, "agent prompt requires sessionId and text");
                await backend.PromptAgentAsync(request.SessionId, request.Text, cancellationToken).ConfigureAwait(false);
                return True;
            }
            case HostProtocol.MethodAgentCancel:
                await backend.CancelAgentAsync(SessionOf(message, "agent cancel requires sessionId"), cancellationToken).ConfigureAwait(false);
                return True;
            case HostProtocol.MethodAgentStop:
                await backend.StopAgentAsync(SessionOf(message, "agent stop requires sessionId"), cancellationToken).ConfigureAwait(false);
                return True;
            case HostProtocol.MethodAgentResume:
                return Serialize(await backend.ResumeAgentAsync(SessionOf(message, "agent resume requires sessionId"), cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteHostSession);
            default:
                throw MethodNotFound(name);
        }
    }

    private static string SessionOf(JsonRpcMessage message, string error)
        => Require(message, HostProtocolJsonContext.Default.RemoteSessionRef, error).SessionId;
}
