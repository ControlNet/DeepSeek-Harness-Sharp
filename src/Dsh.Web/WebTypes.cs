using Dsh.Llm;

namespace Dsh.Web;

public static class WebErrorCodes
{
    public const string InvalidUrl = "WEB_INVALID_URL";
    public const string BlockedUrl = "WEB_BLOCKED_URL";
    public const string ProviderError = "WEB_PROVIDER_ERROR";
    public const string ProviderUnavailable = "WEB_PROVIDER_UNAVAILABLE";
    public const string ProviderAmbiguous = "WEB_PROVIDER_AMBIGUOUS";
    public const string ProviderConfiguredMissing = "WEB_PROVIDER_CONFIGURED_MISSING";
    public const string ProviderConfiguredUnavailable = "WEB_PROVIDER_CONFIGURED_UNAVAILABLE";
    public const string DuplicateProvider = "WEB_DUPLICATE_PROVIDER";
    public const string Aborted = "WEB_ABORTED";
    public const string FetchTimeout = "WEB_FETCH_TIMEOUT";
    public const string FetchTooLarge = "WEB_FETCH_TOO_LARGE";
    public const string UnsupportedContentType = "WEB_UNSUPPORTED_CONTENT_TYPE";
    public const string RedirectBlocked = "WEB_REDIRECT_BLOCKED";
    public const string CredentialMissing = "WEB_PROVIDER_CREDENTIAL_MISSING";
    public const string InvalidArgs = "INVALID_ARGS";
}

public sealed class WebError : HarnessException
{
    public WebError(string message, string code, Exception? innerException = null)
        : base(message, code, innerException)
    {
    }
}

/** Engine names a search provider id; null routes to the configured or the single general-purpose provider. */
public sealed record WebSearchRequest(string Query, int? MaxResults = null, string? Engine = null);

public sealed record WebSearchSource(string Url, string? Title = null, string? Snippet = null, string? PublishedAt = null);

/** Engine is the id of the provider that answered; Limitations states what that engine did not cover or how it was throttled. */
public sealed record WebSearchResult(
    string? Content,
    IReadOnlyList<WebSearchSource> Sources,
    bool Truncated,
    string? Engine = null,
    string? Limitations = null);

public sealed record WebFetchRequest(string Url);

public sealed record WebFetchBody(string Kind, string Content)
{
    public const string Html = "html";
    public const string Text = "text";
}

public sealed record WebFetchResult(string Url, int StatusCode, WebFetchBody Body, bool Truncated);

public interface IWebProvider
{
    string Id { get; }
    bool Available();
}

public interface IWebSearchProvider : IWebProvider
{
    /** One line for the web_search prompt: what this engine covers and when to pick it. */
    string Description => "General web search.";

    /** False for single-site engines: they run only when a call names them, so they never make the default choice ambiguous. */
    bool ImplicitCandidate => true;

    Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal = default);
}

public interface IWebFetchProvider : IWebProvider
{
    Task<WebFetchResult> Fetch(WebFetchRequest request, CancellationToken signal = default);
}
