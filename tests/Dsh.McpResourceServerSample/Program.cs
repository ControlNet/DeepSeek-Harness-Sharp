using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dsh.McpResourceServerSample;

/**
 * Test-only stdio MCP server with fixed resources, one URI template and a tool that adds resources at runtime.
 * Started with --no-resources it serves a single tool and declares no resources capability.
 */
public static class Program
{
    public const string NoResourcesFlag = "--no-resources";
    public const string GuideUri = "docs://guide";
    public const string GuideText = "# Guide\nResource text served by the sample MCP server.";
    public const string LargeUri = "docs://large";
    public const string LargeHead = "LARGE-HEAD";
    public const string LargeTail = "LARGE-TAIL";
    public const int LargeFillerLength = 200_000;
    public const string LogoUri = "files://logo.png";
    public const string LogoPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
    public const string DataUri = "files://data.bin";
    public const int DataLength = 256;
    public const string NoteTemplate = "notes://{name}";

    public static async Task Main(string[] args)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "resource-sample", Version = "1.0" },
        };
        if (args.Contains(NoResourcesFlag))
        {
            options.ToolCollection = [McpServerTool.Create(() => "pong", new McpServerToolCreateOptions { Name = "ping" })];
        }
        else
        {
            var resources = Resources();
            options.ResourceCollection = resources;
            options.ToolCollection = [AddResourceTool(resources)];
        }
        await using var server = McpServer.Create(new StdioServerTransport("resource-sample"), options);
        await server.RunAsync();
    }

    private static McpServerResourceCollection Resources()
        =>
        [
            McpServerResource.Create(
                () => new TextResourceContents { Uri = GuideUri, MimeType = "text/markdown", Text = GuideText },
                new McpServerResourceCreateOptions { UriTemplate = GuideUri, Name = "guide", MimeType = "text/markdown" }),
            McpServerResource.Create(
                () => new TextResourceContents { Uri = LargeUri, MimeType = "text/plain", Text = $"{LargeHead}{new string('x', LargeFillerLength)}{LargeTail}" },
                new McpServerResourceCreateOptions { UriTemplate = LargeUri, Name = "large", MimeType = "text/plain" }),
            McpServerResource.Create(
                () => BlobResourceContents.FromBytes(Convert.FromBase64String(LogoPngBase64), LogoUri, "image/png"),
                new McpServerResourceCreateOptions { UriTemplate = LogoUri, Name = "logo", MimeType = "image/png" }),
            McpServerResource.Create(
                () => BlobResourceContents.FromBytes(Enumerable.Range(0, DataLength).Select(value => (byte)value).ToArray(), DataUri, "application/octet-stream"),
                new McpServerResourceCreateOptions { UriTemplate = DataUri, Name = "data", MimeType = "application/octet-stream" }),
            McpServerResource.Create(
                (string name) => $"note:{name}",
                new McpServerResourceCreateOptions { UriTemplate = NoteTemplate, Name = "note", MimeType = "text/plain" }),
        ];

    private static McpServerTool AddResourceTool(McpServerResourceCollection resources)
        => McpServerTool.Create(
            (string uri, string text) =>
            {
                resources.Add(McpServerResource.Create(
                    () => new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = text },
                    new McpServerResourceCreateOptions { UriTemplate = uri, Name = uri, MimeType = "text/plain" }));
                return "added";
            },
            new McpServerToolCreateOptions { Name = "add_resource", Description = "Adds a text resource." });
}
