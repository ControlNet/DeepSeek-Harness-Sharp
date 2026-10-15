using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Dsh.Web;

/** One GET against a site search API; FailureHint is appended to every error so the user learns how to lift a limit. */
internal sealed record SiteSearchCall(string Engine, Uri Uri, string? FailureHint = null)
{
    public IReadOnlyList<(string Name, string Value)> Headers { get; init; } = [];
}

/**
 * HTTP transport shared by the single-site search engines (GitHub, Stack Overflow).
 * Error text names the endpoint by scheme, host and path only, because API keys may travel in the query string.
 */
internal static class SiteSearchHttp
{
    private const string RateLimitRemainingHeader = "x-ratelimit-remaining";
    private const string RateLimitResetHeader = "x-ratelimit-reset";
    private const int SnippetMaxChars = 300;

    public static HttpClient CreateClient()
        => new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }, disposeHandler: true);

    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, SiteSearchCall call, CancellationToken signal)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, call.Uri);
        request.Headers.TryAddWithoutValidation("user-agent", DeepSeekSearchProvider.UserAgent);
        foreach (var (name, value) in call.Headers)
            request.Headers.TryAddWithoutValidation(name, value);
        using var response = await SendAsync(http, request, call, signal).ConfigureAwait(false);
        var document = await ReadJsonAsync(response, call, signal).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && document is not null)
            return document;
        var detail = document is null ? null : ApiErrorDetail(document.RootElement);
        document?.Dispose();
        var status = response.IsSuccessStatusCode ? "an unparsable body" : $"HTTP {(int)response.StatusCode}";
        var summary = $"{call.Engine} search failed with {status} from {Endpoint(call)}{(detail is { Length: > 0 } ? $": {detail}" : "")}.";
        string?[] parts = [summary, RateLimitReset(response.Headers), call.FailureHint];
        throw new WebError(string.Join(' ', parts.Where(part => part is { Length: > 0 })), WebErrorCodes.ProviderError);
    }

    public static WebError Aborted(string engine) => new($"{engine} search aborted", WebErrorCodes.Aborted);

    /** Collapses whitespace and cuts long excerpts so one result cannot flood the tool output. */
    public static string Excerpt(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= SnippetMaxChars ? collapsed : $"{collapsed[..SnippetMaxChars].TrimEnd()}…";
    }

    public static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static long? OptionalLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    public static string UnixDate(long seconds)
        => DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, SiteSearchCall call, CancellationToken signal)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw Aborted(call.Engine);
        }
        catch (HttpRequestException error)
        {
            throw new WebError($"{call.Engine} search request to {Endpoint(call)} failed: {error.Message}", WebErrorCodes.ProviderError, error);
        }
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, SiteSearchCall call, CancellationToken signal)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(signal).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw Aborted(call.Engine);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /** GitHub reports {message}; Stack Exchange reports {error_name, error_message}. */
    private static string? ApiErrorDetail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (OptionalString(root, "error_message") is { Length: > 0 } stackExchange)
            return OptionalString(root, "error_name") is { Length: > 0 } name ? $"{name}: {stackExchange}" : stackExchange;
        return OptionalString(root, "message");
    }

    private static string? RateLimitReset(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues(RateLimitRemainingHeader, out var remaining) || remaining.FirstOrDefault() != "0")
            return null;
        if (!headers.TryGetValues(RateLimitResetHeader, out var reset) || !long.TryParse(reset.FirstOrDefault(), CultureInfo.InvariantCulture, out var epoch))
            return "The rate limit is exhausted.";
        var resetAt = DateTimeOffset.FromUnixTimeSeconds(epoch).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        return $"The rate limit is exhausted until {resetAt}.";
    }

    private static string Endpoint(SiteSearchCall call) => call.Uri.GetLeftPart(UriPartial.Path);
}
