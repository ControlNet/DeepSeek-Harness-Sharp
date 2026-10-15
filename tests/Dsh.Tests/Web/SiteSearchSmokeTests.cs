using Dsh.Runtime;
using Dsh.Web;

namespace Dsh.Tests;

/**
 * Live calls to the public GitHub and Stack Exchange APIs without credentials, through the plugin entry points and their real HTTP client.
 * They share the LlmSmoke category with the other networked tests so CI and quick runs exclude them.
 */
[Trait("Category", "LlmSmoke")]
public sealed class SiteSearchSmokeTests
{
    [Fact]
    public async Task GitHubRepositories_LiveSearchReturnsRepositories()
    {
        var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var registration = WebSearchGitHub.Apply(ctx);

        var result = await web.Search(new WebSearchRequest("roslyn mcp server", 3, GitHubRepositorySearchProvider.ProviderId), TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Sources);
        Assert.All(result.Sources, source => Assert.StartsWith("https://github.com/", source.Url));
    }

    [Fact]
    public async Task StackOverflow_LiveSearchReturnsQuestions()
    {
        var ctx = new Context();
        var web = new WebRuntime(ctx);
        using var registration = WebSearchStackOverflow.Apply(ctx);

        var result = await web.Search(new WebSearchRequest("roslyn analyzer code fix", 3, StackOverflowSearchProvider.ProviderId), TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Sources);
        Assert.All(result.Sources, source => Assert.StartsWith("https://stackoverflow.com/questions/", source.Url));
    }
}
