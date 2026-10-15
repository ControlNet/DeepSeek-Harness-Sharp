using Dsh.Core;

namespace Dsh.IdeHistory;

public sealed class IdeHistoryRegistry
{
    private readonly List<IIdeHistoryProvider> _providers;

    public IdeHistoryRegistry(IEnumerable<IIdeHistoryProvider> providers)
    {
        _providers = [.. providers];
    }

    public static IdeHistoryRegistry CreateDefault(string? home = null, string? jetBrainsCacheRoot = null)
        => new([new VsCodeLocalHistoryProvider(home), new JetBrainsLocalHistoryProvider(jetBrainsCacheRoot)]);

    public IReadOnlyList<(IIdeHistoryProvider Provider, IdeHistoryStoreInfo Store)> Discover()
        => _providers
            .SelectMany(provider => provider.Discover().Select(store => (provider, store)))
            .ToList();

    public IReadOnlyList<(IIdeHistoryProvider Provider, IdeHistoryStoreInfo Store, IdeHistoryEntry Entry)> Query(string? pathFilter, string? providerFilter, int limit)
    {
        var results = new List<(IIdeHistoryProvider, IdeHistoryStoreInfo, IdeHistoryEntry)>();
        foreach (var (provider, store) in Discover())
        {
            if (providerFilter is { Length: > 0 } && !provider.Name.Contains(providerFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var entry in provider.List(store, pathFilter, limit))
                results.Add((provider, store, entry));
        }
        return [.. results.OrderByDescending(item => item.Item3.Timestamp).Take(limit)];
    }

    public (IIdeHistoryProvider Provider, IdeHistoryStoreInfo Store, IdeHistoryEntry Entry)? Find(string path, long timestamp, string? providerFilter)
    {
        var matches = Query(path, providerFilter, 200)
            .Where(item => item.Entry.Timestamp == timestamp)
            .ToList();
        foreach (var item in matches.Where(item => item.Provider.SupportsContent))
            return item;
        return matches.Count > 0 ? matches[0] : null;
    }

    public IReadOnlyList<string> RetentionNotes()
        => [.. Discover().Select(item => $"{item.Store.Provider}: {item.Store.Retention} @ {item.Store.Location}")];
}
