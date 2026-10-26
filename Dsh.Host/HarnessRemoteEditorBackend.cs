using Dsh.Core;
using Dsh.RemoteHost;

namespace Dsh.Host;

internal sealed class HarnessRemoteEditorBackend(IEditorService service, IEditorClientRegistry? clients, HostPathPolicy paths) : IRemoteEditorBackend
{
    public IEditorService Service { get; } = service;
    public IEditorClientRegistry? Clients { get; } = clients;
    public HostPathPolicy? Paths { get; } = paths;
}
