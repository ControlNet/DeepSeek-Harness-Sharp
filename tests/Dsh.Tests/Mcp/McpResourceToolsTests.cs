using System.Runtime.InteropServices;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Mcp;
using Dsh.Runtime;

namespace Dsh.Tests.Mcp;

/** Connects one McpService to two real child processes of the test-only Dsh.McpResourceServerSample: "res" serves resources, "plain" serves only a tool. */
public sealed class McpResourceServerFixture : IAsyncLifetime
{
    public const string ResourceServer = "res";
    public const string PlainServer = "plain";

    private const string SampleDll = "Dsh.McpResourceServerSample.dll";
    private const string NoResourcesFlag = "--no-resources";

    public Context Ctx { get; } = new();

    public string Home { get; } = TempTree.CreateDirectory("mcp-resources");

    public ToolRuntime Tools { get; private set; } = null!;

    public SystemPrompt Prompt { get; private set; } = null!;

    public McpService Service { get; private set; } = null!;

    /** Launches the sample through the dotnet host that runs this test process, so no apphost or PATH lookup is involved. */
    public static McpServerConfig ServerConfig(string name, bool withResources = true)
    {
        var dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var muxer = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var dll = Path.Combine(AppContext.BaseDirectory, SampleDll);
        return new McpServerConfig(name, "stdio", muxer, withResources ? [dll] : [dll, NoResourcesFlag]);
    }

    public async ValueTask InitializeAsync()
    {
        Ctx.SetOwn("dshHomePath", Home);
        Prompt = new SystemPrompt(Ctx, new SystemPromptConfig());
        Tools = new ToolRuntime(Ctx);
        _ = new FileAttachmentStore(Ctx);
        Service = new McpService(Ctx);
        await Service.ConnectAsync([ServerConfig(ResourceServer), ServerConfig(PlainServer, withResources: false)], TestContext.Current.CancellationToken);
    }

    public Task<ToolExecutionResult> Execute(string name, string arguments)
        => Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonDocument.Parse(arguments).RootElement,
            Signal = TestContext.Current.CancellationToken,
        });

    public async Task<string> SectionText()
    {
        var assembly = await Prompt.Assemble(new AssembleContext());
        return assembly.Sections.Single(section => section.Name == McpResourceTools.SectionName).Text;
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        TempTree.Delete(Home);
    }
}

public sealed class McpResourceToolsTests(McpResourceServerFixture fixture) : IClassFixture<McpResourceServerFixture>
{
    private const string GuideText = "# Guide\nResource text served by the sample MCP server.";
    private const string LogoPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
    private static readonly TimeSpan NotificationDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private static string TextOf(ToolExecutionResult result)
        => string.Join('\n', result.Content.OfType<TextBlock>().Select(block => block.Text));

    private static void AssertSucceeded(ToolExecutionResult result)
        => Assert.False(result.IsError, result is ToolExecutionResult.Failure failure ? failure.Error.Message : null);

    [Fact]
    public void Status_ReportsWhichServersDeclareResources()
    {
        var statuses = fixture.Service.Status().ToDictionary(status => status.Name);

        Assert.True(statuses[McpResourceServerFixture.ResourceServer].Connected, statuses[McpResourceServerFixture.ResourceServer].Error);
        Assert.True(statuses[McpResourceServerFixture.ResourceServer].Resources);
        Assert.True(statuses[McpResourceServerFixture.PlainServer].Connected, statuses[McpResourceServerFixture.PlainServer].Error);
        Assert.False(statuses[McpResourceServerFixture.PlainServer].Resources);
        Assert.Contains("res: stdio connected (1 tools registered, resources)", McpCommand.Format(fixture.Service.Status()));
    }

    [Fact]
    public async Task ListResources_ReturnsTheServersResourcesAttributedToIt()
    {
        var result = await fixture.Execute(McpResourceTools.ListResourcesName, """{ "server": "res" }""");

        AssertSucceeded(result);
        var text = TextOf(result);
        Assert.StartsWith("MCP server: res\n", text);
        Assert.Contains("docs://guide", text);
        Assert.Contains("files://logo.png", text);
        Assert.Contains("files://data.bin", text);
        Assert.Contains("text/markdown", text);
    }

    [Fact]
    public async Task ListResourceTemplates_ReturnsUriTemplates()
    {
        var result = await fixture.Execute(McpResourceTools.ListTemplatesName, """{ "server": "res" }""");

        AssertSucceeded(result);
        Assert.Contains("notes://{name}", TextOf(result));
    }

    [Fact]
    public async Task ReadResource_ReturnsTextDirectly()
    {
        var result = await fixture.Execute(McpResourceTools.ReadResourceName, """{ "server": "res", "uri": "docs://guide" }""");

        AssertSucceeded(result);
        var block = Assert.IsType<TextBlock>(Assert.Single(result.Content));
        Assert.Equal($"MCP resource docs://guide from server \"res\" (text/markdown):\n{GuideText}", block.Text);
    }

    [Fact]
    public async Task ReadResource_ExpandsATemplateUri()
    {
        var result = await fixture.Execute(McpResourceTools.ReadResourceName, """{ "server": "res", "uri": "notes://alpha" }""");

        AssertSucceeded(result);
        Assert.EndsWith("note:alpha", TextOf(result));
    }

    [Fact]
    public async Task ReadResource_StoresAnImageBlobAsAContentAddressedAttachment()
    {
        var result = await fixture.Execute(McpResourceTools.ReadResourceName, """{ "server": "res", "uri": "files://logo.png" }""");

        AssertSucceeded(result);
        var content = Assert.Single(Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("contents").EnumerateArray());
        Assert.False(content.TryGetProperty("blob", out _));
        var expected = Convert.FromBase64String(LogoPngBase64);
        var path = content.GetProperty("path").GetString()!;
        Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        var store = fixture.Ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName)!;
        Assert.True(store.TryRead(content.GetProperty("attachmentId").GetString()!, out var stored));
        Assert.Equal(expected, stored);
        var text = TextOf(result);
        Assert.Contains($"{expected.Length} bytes saved as content-addressed attachment", text);
        Assert.Contains(path, text);
        Assert.Contains("read_image", text);
    }

    [Fact]
    public async Task ReadResource_StoresANonImageBlobWithoutTheImageHint()
    {
        var result = await fixture.Execute(McpResourceTools.ReadResourceName, """{ "server": "res", "uri": "files://data.bin" }""");

        AssertSucceeded(result);
        var content = Assert.Single(Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("contents").EnumerateArray());
        var bytes = await File.ReadAllBytesAsync(content.GetProperty("path").GetString()!, TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, 256).Select(value => (byte)value), bytes);
        Assert.Contains("(application/octet-stream) is binary: 256 bytes", TextOf(result));
        Assert.DoesNotContain("read_image", TextOf(result));
    }

    [Fact]
    public async Task ServerWithoutResourcesCapability_FailsWithAnExplicitMessage()
    {
        var result = await fixture.Execute(McpResourceTools.ListResourcesName, """{ "server": "plain" }""");

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Equal(McpServerHandle.ResourcesUnsupportedCode, failure.Error.Info?.Code);
        Assert.Contains("does not declare the resources capability", failure.Error.Message);
        Assert.Contains("MCP servers without resources (the resource tools fail for them): plain.", await fixture.SectionText());
    }

    [Fact]
    public async Task UnknownServer_ListsTheConfiguredServers()
    {
        var result = await fixture.Execute(McpResourceTools.ReadResourceName, """{ "server": "nope", "uri": "docs://guide" }""");

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Equal(McpResourceTools.UnknownServerCode, failure.Error.Info?.Code);
        Assert.Contains("configured servers: res, plain", failure.Error.Message);
    }

    [Fact]
    public async Task ResourceListChanged_PromptsARelistUntilTheModelListsAgain()
    {
        var uri = $"docs://added-{Guid.NewGuid():N}";
        AssertSucceeded(await fixture.Execute(McpResourceTools.ListResourcesName, """{ "server": "res" }"""));
        Assert.DoesNotContain("resource list changed", await fixture.SectionText());

        AssertSucceeded(await fixture.Execute("mcp_res_add_resource", $$"""{ "uri": "{{uri}}", "text": "fresh" }"""));
        var deadline = DateTime.UtcNow + NotificationDeadline;
        while (!(await fixture.SectionText()).Contains("resource list changed on res") && DateTime.UtcNow < deadline)
            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);

        Assert.Contains("The resource list changed on res after your last listing", await fixture.SectionText());
        var relisted = await fixture.Execute(McpResourceTools.ListResourcesName, """{ "server": "res" }""");
        AssertSucceeded(relisted);
        Assert.Contains(uri, TextOf(relisted));
        Assert.DoesNotContain("resource list changed", await fixture.SectionText());
        Assert.Contains("MCP servers with resources: res.", await fixture.SectionText());
    }
}
