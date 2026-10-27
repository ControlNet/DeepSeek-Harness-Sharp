namespace Dsh.Core;

public interface ILspService
{
    const string ServiceName = "lsp";

    Task<int> RunStdioAsync(Stream input, Stream output, CancellationToken cancellationToken = default);
}
