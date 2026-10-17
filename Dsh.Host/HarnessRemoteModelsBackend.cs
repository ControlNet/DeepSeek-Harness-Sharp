using Dsh.Core;
using Dsh.RemoteHost;

namespace Dsh.Host;

/** Model catalog queries require the real LLM runtime. */
internal sealed class HarnessRemoteModelsBackend(LlmRuntime runtime) : IRemoteModelsBackend
{
    public Task<IReadOnlyList<string>> ListReasoningEffortsAsync(string provider, string model, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>([.. (runtime.ResolveModelInfo(provider, model).Reasoning?.Efforts ?? [])
            .Select(effort => effort.Id.Value)]);

    public Task<IReadOnlyList<string>> ListProviderModelsAsync(string provider, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>([.. runtime.ListModels(provider).Select(model => model.Id)]);
}
