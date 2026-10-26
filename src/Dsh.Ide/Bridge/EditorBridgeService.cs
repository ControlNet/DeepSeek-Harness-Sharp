using Dsh.Core;

namespace Dsh.Ide.Bridge;

/// <summary>Client dispatch is deliberately outside the local launcher's installation and template pipeline.</summary>
public sealed class EditorBridgeService(IEditorService local, IEditorClientDispatcher clients, EditorExecutionContext? executionContext = null) : IEditorService
{
    public IEditorCatalog Catalog => local.Catalog;
    public EditorDecision? LastDecision => local.LastDecision;
    public EditorWorkspace InspectWorkspace(string root) => local.InspectWorkspace(root);
    public EditorBinding? GetBinding(string root, string? sessionId = null) => local.GetBinding(root, sessionId);
    public void Bind(EditorBinding binding) => local.Bind(binding);
    public ValueTask<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        => local.DoctorAsync(workspaceRoot, cancellationToken);

    public async ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ExecutionTarget == EditorExecutionTarget.Automatic)
            request = request with { ExecutionTarget = executionContext?.IsRemoteHost == true ? EditorExecutionTarget.Client : EditorExecutionTarget.Local };
        if (request.ExecutionTarget != EditorExecutionTarget.Client || request.Print)
            return await local.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        var result = await clients.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Error != EditorErrorCode.CLIENT_UNAVAILABLE)
            return result;
        // A copyable host-side command remains available on SSH even without a desktop client.
        var printed = await local.LaunchAsync(request with { Print = true, ExecutionTarget = EditorExecutionTarget.Print }, cancellationToken).ConfigureAwait(false);
        return printed.Success ? printed with { Message = result.Message + " " + printed.Message } : result with { PrintableCommand = printed.PrintableCommand };
    }
}
