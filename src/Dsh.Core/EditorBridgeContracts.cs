namespace Dsh.Core;

public sealed record EditorClientInfo(string Id);

/// <summary>Dispatches typed requests before installation detection or command expansion.</summary>
public interface IEditorClientDispatcher
{
    const string ServiceName = "editorClientDispatcher";
    IReadOnlyList<EditorClientInfo> Clients { get; }
    void SelectClient(string sessionId, string clientId);
    void BindCurrentClient(string sessionId);
    ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default);
}

public interface IEditorClientRegistry : IEditorClientDispatcher
{
    IDisposable RegisterClient(string clientId, Func<EditorRequest, CancellationToken, ValueTask<EditorResult>> handler, CancellationToken lifetime = default);
    IDisposable? EnterClientScope(string clientId);
}

public sealed record EditorRemoteStatus(EditorWorkspace Workspace, EditorBinding? Binding, EditorDecision? LastDecision, IReadOnlyList<EditorClientInfo> Clients);
public sealed record EditorRemoteList(IReadOnlyList<EditorDescriptor> Descriptors, IReadOnlyList<EditorInstallation> Installations);

/// <summary>Set by a Host composition root, never by a model or workspace.</summary>
public sealed class EditorExecutionContext
{
    public const string ServiceName = "editorExecutionContext";
    public bool IsRemoteHost { get; set; }
}
