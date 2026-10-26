using Dsh.Lsp;

namespace Dsh.Ide.Bridge;

public static class EditorLspEntrypoint
{
    public static Task<int> RunAsync(CancellationToken cancellationToken = default)
        => EditorLspTransport.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), new LspServer().HandleRequestAsync, cancellationToken);
}
