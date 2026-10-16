using System.Text.Json;

namespace Dsh.Web;

/** Google Custom Search JSON API for existing customers; the configured cx determines the searched corpus. */
public sealed class GoogleSearchProvider(string? apiKey, string? searchEngineId, HttpClient http) : IWebSearchProvider
{
    public string Id => "google";
    public bool ImplicitCandidate => false;
    public string Description => "Google Custom Search JSON API (existing customers only, ends 2027-01-01). Needs plugins.\"@deepseek-ai/dsh-web-search-google\".apiKey and searchEngineId (cx).";
    public bool Available() => !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(searchEngineId);

    public async Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal = default)
    {
        if (!Available())
            throw new WebError(Description, WebErrorCodes.ProviderUnavailable);
        var count = Math.Clamp(request.MaxResults ?? WebSearchTool.DefaultMaxResults, 1, 10);
        var uri = new Uri($"https://customsearch.googleapis.com/customsearch/v1?q={Uri.EscapeDataString(request.Query)}&key={Uri.EscapeDataString(apiKey!)}&cx={Uri.EscapeDataString(searchEngineId!)}&num={count}");
        using var document = await SiteSearchHttp.GetJsonAsync(http, new SiteSearchCall(Id, uri, "Check the API key, cx, existing-customer eligibility and Google project quota (100 free queries/day; billing up to 10,000/day)."), signal).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("searchInformation", out _))
            throw new WebError("Google search returned an unrecognized response.", WebErrorCodes.ProviderError);
        var sources = root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => ApiWebSearch.Source(item, "link", "snippet")).OfType<WebSearchSource>().ToList()
            : [];
        var truncated = root.TryGetProperty("queries", out var queries) && queries.TryGetProperty("nextPage", out _);
        return new WebSearchResult(null, sources, truncated, Id, "Google Programmable Search Engine: coverage is controlled by the configured cx, not necessarily the whole web. At most 10 results per request and first 100 results accessible. Existing API customers only until 2027-01-01; 100 free queries/day, billed quota up to 10,000/day.");
    }
}

/** Baidu's documented AI search endpoint, returning original web references rather than a generated answer. */
public sealed class BaiduSearchProvider(string? apiKey, HttpClient http) : IWebSearchProvider
{
    public string Id => "baidu";
    public bool ImplicitCandidate => false;
    public string Description => "Baidu web search via Qianfan/AppBuilder. Needs plugins.\"@deepseek-ai/dsh-web-search-baidu\".apiKey (AppBuilder API Key with search access).";
    public bool Available() => !string.IsNullOrWhiteSpace(apiKey);

    public async Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal = default)
    {
        if (!Available())
            throw new WebError(Description, WebErrorCodes.ProviderUnavailable);
        var count = Math.Clamp(request.MaxResults ?? WebSearchTool.DefaultMaxResults, 1, 50);
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["messages"] = new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = request.Query } },
            ["search_source"] = "baidu_search_v2",
            ["resource_type_filter"] = new[] { new Dictionary<string, object> { ["type"] = "web", ["top_k"] = count } },
        });
        var call = new SiteSearchCall(Id, new Uri("https://qianfan.baidubce.com/v2/ai_search/web_search"), "Check the AppBuilder API Key, search access and account quota/billing in the Baidu console.")
        {
            JsonBody = body,
            Headers = [("authorization", $"Bearer {apiKey}")],
        };
        using var document = await SiteSearchHttp.GetJsonAsync(http, call, signal).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("references", out var references) || references.ValueKind != JsonValueKind.Array)
            throw new WebError("Baidu search returned an unrecognized response without web references.", WebErrorCodes.ProviderError);
        var sources = references.EnumerateArray().Select(item => ApiWebSearch.Source(item, "url", "content", "date")).OfType<WebSearchSource>().ToList();
        return new WebSearchResult(null, sources, sources.Count >= count, Id, "Baidu baidu_search_v2 web references only (no images/video); at most 50 references per request. Account-specific free/paid quota and request-rate limits apply; consult the Baidu console. A full page may have more matches.");
    }
}

internal static class ApiWebSearch
{
    public static WebSearchSource? Source(JsonElement item, string urlKey, string snippetKey, string? dateKey = null)
    {
        var url = SiteSearchHttp.OptionalString(item, urlKey);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return null;
        var snippet = SiteSearchHttp.OptionalString(item, snippetKey);
        return new WebSearchSource(uri.AbsoluteUri, SiteSearchHttp.OptionalString(item, "title"), snippet is null ? null : SiteSearchHttp.Excerpt(snippet), dateKey is null ? null : SiteSearchHttp.OptionalString(item, dateKey));
    }
}
