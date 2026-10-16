using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Dsh.Runtime;

namespace Dsh.Web;

/** Bing's public RSS search entry, not the retired Azure Bing Search API. */
public sealed class BingSearchProvider(HttpClient http, string? market = null) : IWebSearchProvider
{
    public string Id => "bing";
    public bool ImplicitCandidate => false;
    public string Description => "Bing public RSS search (personal, non-commercial RSS reading only; other use requires Microsoft permission). No key; ranking varies by region.";
    public bool Available() => true;

    public async Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal = default)
    {
        var marketQuery = string.IsNullOrWhiteSpace(market) ? "" : $"&mkt={Uri.EscapeDataString(market)}";
        var uri = new Uri($"https://www.bing.com/search?q={Uri.EscapeDataString(request.Query)}&format=rss{marketQuery}");
        var text = await SiteSearchHttp.GetTextAsync(http, new SiteSearchCall(Id, uri, "The public RSS entry has no guaranteed quota/SLA and may block automated requests; Azure Bing Search APIs retired on 2025-08-11."), signal).ConfigureAwait(false);
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var root = XDocument.Load(reader);
            if (root.Root?.Name != "rss" || root.Root.Element("channel") is not { } channel)
                throw new XmlException("Missing RSS channel");
            var sources = channel.Elements("item").Select(item =>
            {
                if (!Uri.TryCreate(item.Element("link")?.Value, UriKind.Absolute, out var link) || link.Scheme is not ("https" or "http"))
                    return null;
                return new WebSearchSource(link.AbsoluteUri, item.Element("title")?.Value,
                    SiteSearchHttp.Excerpt(HtmlToMarkdown.Convert(item.Element("description")?.Value ?? "")), item.Element("pubDate")?.Value);
            }).OfType<WebSearchSource>().ToList();
            return new WebSearchResult(null, sources, sources.Count > 0, Id, $"Bing public RSS, market {market ?? "site default"}; a limited first page with no total count or pagination, not Azure's retired API. RSS terms restrict use to personal, non-commercial RSS reading; other use requires Microsoft permission. Ranking/coverage may differ from interactive Bing. No published automated quota or SLA; may be rate-limited or blocked.");
        }
        catch (XmlException)
        {
            throw new WebError("Bing search returned non-RSS content (possibly a block/challenge page).", WebErrorCodes.ProviderError);
        }
    }
}

/** DuckDuckGo's public non-JavaScript HTML search; challenges are errors, never empty search results. */
public sealed partial class DuckDuckGoSearchProvider(HttpClient http, string region = "wt-wt") : IWebSearchProvider
{
    public string Id => "duckduckgo";
    public bool ImplicitCandidate => false;
    public string Description => "DuckDuckGo public HTML web search (no key; automated requests may receive a challenge).";
    public bool Available() => true;

    public async Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal = default)
    {
        var uri = new Uri($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(request.Query)}&kl={Uri.EscapeDataString(region)}");
        var html = await SiteSearchHttp.GetTextAsync(http, new SiteSearchCall(Id, uri, "DuckDuckGo may rate-limit or require a human challenge; no API key removes this public-entry restriction."), signal).ConfigureAwait(false);
        if (html.Contains("anomaly.js", StringComparison.OrdinalIgnoreCase) || html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase))
            throw new WebError("DuckDuckGo search requires a human challenge; automated results are unavailable. Try another configured engine.", WebErrorCodes.ProviderError);
        var sources = new List<WebSearchSource>();
        foreach (Match anchor in ResultLink().Matches(html))
        {
            var attributes = anchor.Groups[1].Value;
            if (!ResultClass().IsMatch(attributes))
                continue;
            var href = Href().Match(attributes);
            if (!href.Success)
                continue;
            var url = WebUtility.HtmlDecode(href.Groups[1].Value);
            if (url.StartsWith("//", StringComparison.Ordinal))
                url = $"https:{url}";
            if (!Uri.TryCreate(new Uri("https://html.duckduckgo.com"), url, out var link))
                continue;
            if (link.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
            {
                var target = link.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).FirstOrDefault(pair => pair[0] == "uddg");
                if (target is not { Length: 2 } || !Uri.TryCreate(Uri.UnescapeDataString(target[1]), UriKind.Absolute, out link))
                    continue;
            }
            if (link.Scheme is "https" or "http")
                sources.Add(new WebSearchSource(link.AbsoluteUri, HtmlToMarkdown.Convert(anchor.Groups[2].Value)));
        }
        if (sources.Count == 0 && !html.Contains("no-results", StringComparison.OrdinalIgnoreCase))
            throw new WebError("DuckDuckGo search returned an unrecognized HTML page, not search results.", WebErrorCodes.ProviderError);
        return new WebSearchResult(null, sources.DistinctBy(source => source.Url).ToList(), html.Contains("next", StringComparison.OrdinalIgnoreCase) && sources.Count > 0, Id,
            $"DuckDuckGo public HTML search, region {region}; first-page links/titles only, no generated answer. No published automated quota or SLA; rate limits, challenges and markup changes can prevent searches.");
    }

    [GeneratedRegex("<a\\b([^>]+)>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ResultLink();
    [GeneratedRegex("class\\s*=\\s*[\"'][^\"']*\\bresult__a\\b[^\"']*[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex ResultClass();
    [GeneratedRegex("href\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Href();
}

/** Each general search engine is an independent plugin, reusing one transport and configuration boundary. */
public static class WebSearchGeneral
{
    public static IDisposable Apply(Context ctx, string engine, object? config = null)
    {
        var settings = config switch
        {
            null => new Dictionary<string, object?>(),
            IReadOnlyDictionary<string, object?> dict => dict,
            _ => throw new ArgumentException($"web-search-{engine} config must be an object", nameof(config)),
        };
        var web = ctx.Get<WebRuntime>(WebRuntime.ServiceName) ?? throw new InvalidOperationException("web service is not registered");
        var http = SiteSearchHttp.CreateClient(ctx);
        IWebSearchProvider provider = engine switch
        {
            "google" => new GoogleSearchProvider(settings.GetValueOrDefault("apiKey") as string, settings.GetValueOrDefault("searchEngineId") as string, http),
            "baidu" => new BaiduSearchProvider(settings.GetValueOrDefault("apiKey") as string, http),
            "bing" => new BingSearchProvider(http, settings.GetValueOrDefault("market") as string),
            "duckduckgo" => new DuckDuckGoSearchProvider(http, settings.GetValueOrDefault("region") as string ?? "wt-wt"),
            _ => throw new ArgumentException($"Unknown general search engine '{engine}'", nameof(engine)),
        };
        return new WebDisposable(web.RegisterSearchProvider(provider), http);
    }
}
