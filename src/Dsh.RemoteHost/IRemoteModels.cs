namespace Dsh.RemoteHost;

/** Optional provider model and reasoning effort discovery capabilities. */
public interface IRemoteModels
{
    Task<IReadOnlyList<string>> ListReasoningEffortsAsync(string provider, string model, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListProviderModelsAsync(string provider, CancellationToken cancellationToken = default);
}
