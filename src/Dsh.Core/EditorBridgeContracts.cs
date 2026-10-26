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

/// <summary>Execution routing owned by the composition root and its authenticated UI clients, never by a model or workspace.</summary>
public sealed class EditorExecutionContext
{
    public const string ServiceName = "editorExecutionContext";
    private readonly AsyncLocal<ITerminalHost?> _terminal = new();
    public ITerminalHost? TerminalHost => _terminal.Value;
    public bool IsRemoteHost { get; set; }

    public IEditorService BindTerminal(IEditorService editors, ITerminalHost terminal)
        => new TerminalService(this, editors, terminal);

    private sealed class TerminalService(EditorExecutionContext context, IEditorService editors, ITerminalHost terminal) : IEditorService
    {
        public IEditorCatalog Catalog => editors.Catalog;
        public EditorDecision? LastDecision => editors.LastDecision;
        public EditorWorkspace InspectWorkspace(string root) => editors.InspectWorkspace(root);
        public EditorBinding? GetBinding(string root, string? sessionId = null) => editors.GetBinding(root, sessionId);
        public void Bind(EditorBinding binding) => editors.Bind(binding);
        public async ValueTask<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        {
            var previous = context._terminal.Value;
            context._terminal.Value = terminal;
            try
            {
                return await editors.DoctorAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                context._terminal.Value = previous;
            }
        }

        public async ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default)
        {
            var previous = context._terminal.Value;
            context._terminal.Value = terminal;
            try
            {
                return await editors.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                context._terminal.Value = previous;
            }
        }
    }
}
