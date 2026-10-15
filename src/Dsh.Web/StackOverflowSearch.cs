using System.Net;
using System.Text.Json;
using Dsh.Runtime;

namespace Dsh.Web;

/** Stack Overflow questions through the Stack Exchange API /search/advanced endpoint. */
public sealed class StackOverflowSearchProvider(string? key, HttpClient http) : IWebSearchProvider
{
    public const string ProviderId = "stackoverflow";
    public const string ApiBase = "https://api.stackexchange.com/2.3";
    public const string KeySetting = "plugins.\"@deepseek-ai/dsh-web-search-stackoverflow\".key";
    public const int MaxPageSize = 100;
    private const string Site = "stackoverflow";
    private const string BodyFilter = "withbody";

    private readonly Lock _gate = new();
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;

    public string Id => ProviderId;

    public string Description
        => "Stack Overflow questions (find known errors, how-to answers and tool recommendations). Write plain keywords.";

    public bool ImplicitCandidate => false;

    public bool Available() => true;

    public async Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal = default)
    {
        await WaitForBackoffAsync(signal).ConfigureAwait(false);
        var pageSize = Math.Clamp(request.MaxResults ?? WebSearchTool.DefaultMaxResults, 1, MaxPageSize);
        var credential = key is { Length: > 0 } ? $"&key={Uri.EscapeDataString(key)}" : "";
        var uri = new Uri($"{ApiBase}/search/advanced?order=desc&sort=relevance&q={Uri.EscapeDataString(request.Query)}&site={Site}&pagesize={pageSize}&filter={BodyFilter}{credential}");
        var hint = key is { Length: > 0 } ? null : $"Set {KeySetting} in settings.yaml to raise the Stack Exchange quota.";
        using var document = await SiteSearchHttp.GetJsonAsync(http, new SiteSearchCall(Id, uri, hint), signal).ConfigureAwait(false);
        var result = Map(document.RootElement);
        if (SiteSearchHttp.OptionalLong(document.RootElement, "backoff") is { } seconds)
        {
            lock (_gate)
                _backoffUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
        return result;
    }

    /** Maps a /search/advanced body; public so captured API responses can be checked without a network. */
    public WebSearchResult Map(JsonElement response)
    {
        var items = response.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(MapQuestion).OfType<WebSearchSource>().ToList()
            : [];
        var hasMore = response.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
        var quota = key is { Length: > 0 }
            ? "With the configured key the quota is 10,000 requests per day."
            : $"Without a key the quota is 300 requests per day per IP, and every query in a call uses one; set {KeySetting} to raise it to 10,000.";
        var backoff = SiteSearchHttp.OptionalLong(response, "backoff") is { } seconds
            ? $" Stack Exchange asked clients to pause {seconds} s; the next search waits for it."
            : "";
        var limitations = $"Searches Stack Overflow questions only (title, body and tags); open a result to read its answers. {quota}{backoff}";
        return new WebSearchResult(null, items, hasMore, Id, limitations);
    }

    private static WebSearchSource? MapQuestion(JsonElement item)
    {
        if (SiteSearchHttp.OptionalString(item, "link") is not { Length: > 0 } url)
            return null;
        var tags = item.TryGetProperty("tags", out var array) && array.ValueKind == JsonValueKind.Array
            ? string.Join(", ", array.EnumerateArray().Select(tag => tag.GetString()).OfType<string>())
            : "";
        var body = SiteSearchHttp.OptionalString(item, "body");
        string?[] facts =
        [
            $"{SiteSearchHttp.OptionalLong(item, "score") ?? 0} votes",
            $"{SiteSearchHttp.OptionalLong(item, "answer_count") ?? 0} answers",
            SiteSearchHttp.OptionalLong(item, "accepted_answer_id") is null ? null : "accepted answer",
            tags.Length > 0 ? $"tags: {tags}" : null,
            body is { Length: > 0 } ? HtmlToMarkdown.Convert(body) : null,
        ];
        var created = SiteSearchHttp.OptionalLong(item, "creation_date");
        var title = SiteSearchHttp.OptionalString(item, "title");
        return new WebSearchSource(
            url,
            title is null ? null : WebUtility.HtmlDecode(title),
            SiteSearchHttp.Excerpt(string.Join(" · ", facts.Where(fact => fact is { Length: > 0 }))),
            created is null ? null : SiteSearchHttp.UnixDate(created.Value));
    }

    private async Task WaitForBackoffAsync(CancellationToken signal)
    {
        TimeSpan wait;
        lock (_gate)
            wait = _backoffUntil - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
            return;
        try
        {
            await Task.Delay(wait, signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw SiteSearchHttp.Aborted(Id);
        }
    }
}

/** Plugin entry: registers the stackoverflow engine with an optional Stack Exchange key. */
public static class WebSearchStackOverflow
{
    public const string KeyKey = "key";

    public static IDisposable Apply(Context ctx, object? config = null)
    {
        var key = config switch
        {
            null => null,
            IReadOnlyDictionary<string, object?> dict => dict.GetValueOrDefault(KeyKey) as string,
            _ => throw new ArgumentException("web-search-stackoverflow config must be an object", nameof(config)),
        };
        var web = ctx.Get<WebRuntime>(WebRuntime.ServiceName)
            ?? throw new InvalidOperationException("web service is not registered");
        var http = SiteSearchHttp.CreateClient();
        var resolved = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        return new WebDisposable(web.RegisterSearchProvider(new StackOverflowSearchProvider(resolved, http)), http);
    }
}
