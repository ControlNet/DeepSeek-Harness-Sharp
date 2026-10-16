using System.Text.Json;
using Dsh.Runtime;

namespace Dsh.Web;

/**
 * GitHub REST search engines.
 * Repository and code search accept different qualifiers, so each search type is its own engine and the model writes queries for one of them.
 */
public abstract class GitHubSearchProvider(string? token, HttpClient http) : IWebSearchProvider
{
    public const string ApiBase = "https://api.github.com";
    public const string ApiVersion = "2022-11-28";
    public const string TokenSetting = "plugins.\"@deepseek-ai/dsh-web-search-github\".token";
    public const int MaxPerPage = 100;

    public abstract string Id { get; }

    public abstract string Description { get; }

    public bool ImplicitCandidate => false;

    protected bool Authenticated => token is { Length: > 0 };

    protected abstract string SearchPath { get; }

    protected abstract string MediaType { get; }

    public abstract bool Available();

    public async Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal = default)
    {
        var perPage = Math.Clamp(request.MaxResults ?? WebSearchTool.DefaultMaxResults, 1, MaxPerPage);
        var uri = new Uri($"{ApiBase}/search/{SearchPath}?q={Uri.EscapeDataString(request.Query)}&per_page={perPage}");
        List<(string, string)> headers = [("accept", MediaType), ("x-github-api-version", ApiVersion)];
        if (Authenticated)
            headers.Add(("authorization", $"Bearer {token}"));
        var hint = Authenticated ? null : $"Set {TokenSetting} in settings.yaml to raise the GitHub rate limit.";
        using var document = await SiteSearchHttp.GetJsonAsync(http, new SiteSearchCall(Id, uri, hint) { Headers = headers }, signal).ConfigureAwait(false);
        return Map(document.RootElement);
    }

    /** Maps a search response body; public so captured API responses can be checked without a network. */
    public WebSearchResult Map(JsonElement response)
    {
        var items = response.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(MapItem).OfType<WebSearchSource>().ToList()
            : [];
        var total = SiteSearchHttp.OptionalLong(response, "total_count") ?? items.Count;
        var incomplete = response.TryGetProperty("incomplete_results", out var flag) && flag.ValueKind == JsonValueKind.True;
        var limitations = incomplete ? $"{Limitations} GitHub reported incomplete results because the search timed out." : Limitations;
        return new WebSearchResult(null, items, total > items.Count, Id, limitations);
    }

    protected abstract string Limitations { get; }

    protected abstract WebSearchSource? MapItem(JsonElement item);
}

/** Searches repository names, descriptions and topics; works without a token at the lower anonymous rate limit. */
public sealed class GitHubRepositorySearchProvider(string? token, HttpClient http) : GitHubSearchProvider(token, http)
{
    public const string ProviderId = "github-repos";

    public override string Id => ProviderId;

    public override string Description
        => "GitHub repositories (find existing tools, libraries, MCP servers and their docs). Qualifiers: language:, topic:, stars:>N, pushed:>YYYY-MM-DD, in:readme, org:, user:.";

    protected override string SearchPath => "repositories";

    protected override string MediaType => "application/vnd.github+json";

    protected override string Limitations
        => $"GitHub repository search matches repository names, descriptions and topics (add in:readme to search README files); it does not search code, issues or wikis. {(Authenticated
            ? "Authenticated: 30 search requests per minute."
            : $"Unauthenticated: 10 search requests per minute per IP, and every query in a call uses one; set {TokenSetting} to raise it to 30.")}";

    public override bool Available() => true;

    protected override WebSearchSource? MapItem(JsonElement item)
    {
        if (SiteSearchHttp.OptionalString(item, "html_url") is not { Length: > 0 } url)
            return null;
        var stars = SiteSearchHttp.OptionalLong(item, "stargazers_count");
        var topics = item.TryGetProperty("topics", out var array) && array.ValueKind == JsonValueKind.Array
            ? string.Join(", ", array.EnumerateArray().Select(topic => topic.GetString()).OfType<string>())
            : "";
        var pushed = SiteSearchHttp.OptionalString(item, "pushed_at");
        string?[] facts =
        [
            SiteSearchHttp.OptionalString(item, "description"),
            stars is null ? null : $"{stars} stars",
            SiteSearchHttp.OptionalString(item, "language"),
            topics.Length > 0 ? $"topics: {topics}" : null,
            pushed is { Length: >= 10 } ? $"last push {pushed[..10]}" : null,
            item.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True ? "archived" : null,
        ];
        var snippet = string.Join(" · ", facts.Where(fact => fact is { Length: > 0 }));
        return new WebSearchSource(url, SiteSearchHttp.OptionalString(item, "full_name"), snippet.Length > 0 ? SiteSearchHttp.Excerpt(snippet) : null);
    }
}

/** Searches file contents on default branches; GitHub requires a token for this endpoint. */
public sealed class GitHubCodeSearchProvider(string? token, HttpClient http) : GitHubSearchProvider(token, http)
{
    public const string ProviderId = "github-code";

    public override string Id => ProviderId;

    public override string Description
        => $"GitHub file contents (find usage examples, config files and docs inside repositories). Qualifiers: repo:owner/name, path:, filename:, extension:, language:, org:. Needs {TokenSetting} in settings.yaml.";

    protected override string SearchPath => "code";

    protected override string MediaType => "application/vnd.github.text-match+json";

    protected override string Limitations
        => "GitHub code search covers default branches only, skips files larger than 384 KB, and returns files rather than repositories. Limited to 10 requests per minute, and every query in a call uses one.";

    public override bool Available() => Authenticated;

    protected override WebSearchSource? MapItem(JsonElement item)
    {
        if (SiteSearchHttp.OptionalString(item, "html_url") is not { Length: > 0 } url)
            return null;
        var repository = item.TryGetProperty("repository", out var repo) ? SiteSearchHttp.OptionalString(repo, "full_name") : null;
        var path = SiteSearchHttp.OptionalString(item, "path");
        var fragment = item.TryGetProperty("text_matches", out var matches) && matches.ValueKind == JsonValueKind.Array
            ? matches.EnumerateArray().Select(match => SiteSearchHttp.OptionalString(match, "fragment")).FirstOrDefault(text => text is { Length: > 0 })
            : null;
        var title = repository is null ? path : $"{repository}: {path}";
        return new WebSearchSource(url, title, fragment is null ? null : SiteSearchHttp.Excerpt(fragment));
    }
}

/** Plugin entry: registers github-repos always and github-code when a token is configured. */
public static class WebSearchGitHub
{
    public const string TokenKey = "token";

    public static IDisposable Apply(Context ctx, object? config = null)
    {
        var token = config switch
        {
            null => null,
            IReadOnlyDictionary<string, object?> dict => dict.GetValueOrDefault(TokenKey) as string,
            _ => throw new ArgumentException("web-search-github config must be an object", nameof(config)),
        };
        var web = ctx.Get<WebRuntime>(WebRuntime.ServiceName)
            ?? throw new InvalidOperationException("web service is not registered");
        var http = SiteSearchHttp.CreateClient(ctx);
        var resolved = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        return new WebDisposable(
            web.RegisterSearchProvider(new GitHubRepositorySearchProvider(resolved, http)),
            web.RegisterSearchProvider(new GitHubCodeSearchProvider(resolved, http)),
            http);
    }
}
