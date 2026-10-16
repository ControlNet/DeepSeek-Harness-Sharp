using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dsh.Runtime;
using Dsh.Web;

namespace Dsh.Tests;

public sealed class GeneralSearchEngineTests
{
    private const string TestKey = "component-test-key-not-a-credential";

    private sealed class ResponseHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        public HttpMethod? Method { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return response;
        }
    }

    [Fact]
    public async Task Google_UsesConfiguredCorpusAndMapsSources()
    {
        var handler = new ResponseHandler("""{"searchInformation":{"totalResults":"2"},"queries":{"nextPage":[{}]},"items":[{"link":"https://dotnet.microsoft.com/","title":".NET","snippet":"Build with .NET"}]}""");
        var result = await new GoogleSearchProvider(TestKey, "configured-cx", new HttpClient(handler)).Search(new WebSearchRequest("C# & .NET", 50), TestContext.Current.CancellationToken);
        Assert.Equal("google", result.Engine);
        Assert.Equal("https://dotnet.microsoft.com/", Assert.Single(result.Sources).Url);
        Assert.True(result.Truncated);
        Assert.Equal("customsearch.googleapis.com", handler.Uri!.Host);
        Assert.Contains("key=component-test-key-not-a-credential", handler.Uri.Query);
        Assert.Contains("cx=configured-cx", handler.Uri.Query);
        Assert.Contains("num=10", handler.Uri.Query);
        Assert.Contains("q=C%23%20%26%20.NET", handler.Uri.Query);
        Assert.Contains("2027-01-01", result.Limitations);
    }

    [Fact]
    public async Task Baidu_PostsDocumentedRequestAndMapsReferences()
    {
        var handler = new ResponseHandler("""{"references":[{"title":"百度千帆","url":"https://cloud.baidu.com/","content":"实时信息","date":"2026-10-16","type":"web"}]}""");
        var result = await new BaiduSearchProvider(TestKey, new HttpClient(handler)).Search(new WebSearchRequest("百度千帆", 99), TestContext.Current.CancellationToken);
        Assert.Equal("baidu", result.Engine);
        Assert.Equal("https://cloud.baidu.com/", Assert.Single(result.Sources).Url);
        Assert.Equal("2026-10-16", result.Sources[0].PublishedAt);
        Assert.Equal("Bearer component-test-key-not-a-credential", handler.Authorization);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://qianfan.baidubce.com/v2/ai_search/web_search", handler.Uri!.AbsoluteUri);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("百度千帆", request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("baidu_search_v2", request.RootElement.GetProperty("search_source").GetString());
        Assert.Equal(50, request.RootElement.GetProperty("resource_type_filter")[0].GetProperty("top_k").GetInt32());
    }

    [Fact]
    public async Task Bing_MapsPublicRssAndMarket()
    {
        const string body = """<rss version="2.0"><channel><title>Bing: roslyn</title><item><title>.NET</title><link>https://dotnet.microsoft.com/</link><description>Build with .NET</description><pubDate>Fri, 16 Oct 2026 00:00:00 GMT</pubDate></item></channel></rss>""";
        var handler = new ResponseHandler(body);
        var result = await new BingSearchProvider(new HttpClient(handler), "zh-CN").Search(new WebSearchRequest("roslyn"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(result.Sources);
        Assert.All(result.Sources, source => Assert.True(Uri.IsWellFormedUriString(source.Url, UriKind.Absolute)));
        Assert.Equal("bing", result.Engine);
        Assert.Contains("format=rss", handler.Uri!.Query);
        Assert.Contains("mkt=zh-CN", handler.Uri.Query);
        Assert.Contains("no total count", result.Limitations);
    }

    [Fact]
    public async Task DuckDuckGo_ExtractsResultLinksNotAdsOrNavigation()
    {
        var handler = new ResponseHandler("""<html><a href="/">Home</a><a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fdotnet.microsoft.com%2F&amp;rut=tracking"><b>.NET</b> &amp; C#</a><a href="https://advert.test/">Ad</a></html>""");
        var result = await new DuckDuckGoSearchProvider(new HttpClient(handler), "us-en").Search(new WebSearchRequest("C#"), TestContext.Current.CancellationToken);
        Assert.Equal("https://dotnet.microsoft.com/", Assert.Single(result.Sources).Url);
        Assert.Equal("**.NET** & C#", result.Sources[0].Title);
        Assert.Contains("kl=us-en", handler.Uri!.Query);
        Assert.Contains("first-page", result.Limitations);
    }

    [Theory]
    [InlineData("bing", "<html>challenge</html>")]
    [InlineData("duckduckgo", "<form id=\"challenge-form\">human challenge</form>")]
    [InlineData("duckduckgo", "<html>unrecognized</html>")]
    [InlineData("baidu", "{\"code\":216003,\"message\":\"Authentication error\"}")]
    public async Task BlockOrApiError_IsNeverAnEmptySuccessfulSearch(string engine, string body)
    {
        using var http = new HttpClient(new ResponseHandler(body));
        IWebSearchProvider provider = engine switch
        {
            "bing" => new BingSearchProvider(http),
            "duckduckgo" => new DuckDuckGoSearchProvider(http),
            _ => new BaiduSearchProvider(TestKey, http),
        };
        var error = await Assert.ThrowsAsync<WebError>(() => provider.Search(new WebSearchRequest("q"), TestContext.Current.CancellationToken));
        Assert.Equal(WebErrorCodes.ProviderError, error.Code);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("baidu")]
    [InlineData("bing")]
    [InlineData("duckduckgo")]
    public async Task RateLimits_AreExplicitAndDoNotExposeCredentials(string engine)
    {
        using var http = new HttpClient(new ResponseHandler($"{{\"error\":{{\"message\":\"quota {TestKey}\"}}}}", HttpStatusCode.TooManyRequests));
        IWebSearchProvider provider = engine switch
        {
            "google" => new GoogleSearchProvider(TestKey, "cx", http),
            "baidu" => new BaiduSearchProvider(TestKey, http),
            "bing" => new BingSearchProvider(http),
            _ => new DuckDuckGoSearchProvider(http),
        };
        var error = await Assert.ThrowsAsync<WebError>(() => provider.Search(new WebSearchRequest("q"), TestContext.Current.CancellationToken));
        Assert.Equal(WebErrorCodes.ProviderError, error.Code);
        Assert.Contains("HTTP 429", error.Message);
        Assert.Contains("Retry after 17", error.Message);
        Assert.DoesNotContain(TestKey, error.Message);
    }

    [Fact]
    public async Task Engines_AreIndependentAndMissingCredentialsNameTheirSettings()
    {
        var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var google = WebSearchGeneral.Apply(ctx, "google");
        using var baidu = WebSearchGeneral.Apply(ctx, "baidu");
        using var bing = WebSearchGeneral.Apply(ctx, "bing");
        var duck = WebSearchGeneral.Apply(ctx, "duckduckgo");
        Assert.Equal(["bing", "duckduckgo"], web.SearchEngines().Select(engine => engine.Id));
        duck.Dispose();
        Assert.Equal(["bing"], web.SearchEngines().Select(engine => engine.Id));
        foreach (var engine in new[] { "google", "baidu" })
        {
            var error = await Assert.ThrowsAsync<WebError>(() => web.Search(new WebSearchRequest("q", Engine: engine), TestContext.Current.CancellationToken));
            Assert.Equal(WebErrorCodes.InvalidArgs, error.Code);
            Assert.Contains($"dsh-web-search-{engine}", error.Message);
            Assert.Contains("apiKey", error.Message);
        }
        Assert.False(new GoogleSearchProvider(TestKey, null, new HttpClient()).Available());
    }

    [Theory]
    [InlineData("google", "customsearch.googleapis.com")]
    [InlineData("baidu", "qianfan.baidubce.com")]
    [InlineData("bing", "www.bing.com")]
    [InlineData("duckduckgo", "html.duckduckgo.com")]
    [InlineData("github-repos", "api.github.com")]
    [InlineData("github-code", "api.github.com")]
    [InlineData("stackoverflow", "api.stackexchange.com")]
    [InlineData("deepseek-official", "api.deepseek.com")]
    public async Task EverySearchPlugin_UsesSharedOutboundProxy(string engine, string host)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new OutboundProxy();
        proxy.Configure($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", []);
        var ctx = new Context();
        ctx.SetOwn(OutboundProxy.PropName, proxy);
        var web = new WebRuntime(ctx);
        var settings = new Dictionary<string, object?> { ["apiKey"] = TestKey, ["searchEngineId"] = "cx", ["token"] = TestKey };
        using var registration = engine switch
        {
            "github-repos" or "github-code" => WebSearchGitHub.Apply(ctx, settings),
            "stackoverflow" => WebSearchStackOverflow.Apply(ctx),
            "deepseek-official" => WebSearchDeepseek.Apply(ctx, settings),
            _ => WebSearchGeneral.Apply(ctx, engine, settings),
        };
        var search = web.Search(new WebSearchRequest("q", Engine: engine), deadline.Token);
        using var client = await listener.AcceptTcpClientAsync(deadline.Token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var line = await reader.ReadLineAsync(deadline.Token);
        Assert.Equal($"CONNECT {host}:443 HTTP/1.1", line);
        await stream.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), deadline.Token);
        var error = await Assert.ThrowsAsync<WebError>(() => search);
        Assert.Equal(WebErrorCodes.ProviderError, error.Code);
    }
}
