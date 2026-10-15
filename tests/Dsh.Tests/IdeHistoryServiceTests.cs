using Dsh.IdeHistory;
using Dsh.Runtime;
using Dsh.Core;

namespace Dsh.Tests;

/** §20: 工作区门控 + 服务化(与 checkpoints 无依赖) + 配置解析。 */
public sealed class IdeHistoryServiceTests
{
    /** 路径过滤要无视分隔符/URI 前缀: TUI 传本机反斜杠路径, JetBrains 存正斜杠、VSCode 存 file:///c:/…。 */
    [Fact]
    public void PathFilter_Normalizes_Separators_And_UriPrefixes()
    {
        Assert.True(IdeHistoryPathFilter.Matches(@"C:/Users/dev/proj/app.cs", @"C:\Users\dev\proj"));
        Assert.True(IdeHistoryPathFilter.Matches(@"C:\Users\dev\proj\app.cs", "C:/Users/dev/proj"));
        Assert.True(IdeHistoryPathFilter.Matches("file:///C:/Users/dev/proj/app.cs", @"C:\Users\dev\proj\"));
        Assert.True(IdeHistoryPathFilter.Matches(@"c:\users\dev\proj\app.cs", @"C:\Users\Dev\Proj"));
        Assert.True(IdeHistoryPathFilter.Matches("/home/dev/proj/app.cs", "/home/dev/proj"));
        Assert.False(IdeHistoryPathFilter.Matches(@"C:\Users\dev\other\app.cs", @"C:\Users\dev\proj"));
        Assert.False(IdeHistoryPathFilter.Matches(null, @"C:\Users\dev\proj"));
        // 不传过滤 = 全通过。
        Assert.True(IdeHistoryPathFilter.Matches(@"C:\any\where.cs", null));
        Assert.True(IdeHistoryPathFilter.Matches(@"C:\any\where.cs", "  "));
    }

    [Fact]
    public void Detector_FindsMarkers_And_NoneWithoutThem()
    {
        var root = TempTree.CreateDirectory("workspace-detect");
        try
        {
            var plain = Path.Combine(root, "plain");
            Directory.CreateDirectory(plain);
            // 项目里没有 .vscode/.zed(仓库根有 .git, 检索在此收口, 不继承 C:\Users\k6789 之类上层目录的标记)。
            var plainKind = WorkspaceIdeDetector.Detect(plain);
            Assert.False(plainKind.HasFlag(IdeWorkspaceKind.VsCode));
            Assert.False(plainKind.HasFlag(IdeWorkspaceKind.Zed));

            var vscode = Path.Combine(root, "vscode");
            Directory.CreateDirectory(Path.Combine(vscode, ".vscode"));
            Assert.True(WorkspaceIdeDetector.Detect(vscode).HasFlag(IdeWorkspaceKind.VsCode));

            var jetbrains = Path.Combine(root, "jetbrains");
            Directory.CreateDirectory(Path.Combine(jetbrains, ".idea"));
            Assert.True(WorkspaceIdeDetector.Detect(jetbrains).HasFlag(IdeWorkspaceKind.JetBrains));

            var zed = Path.Combine(root, "zed");
            Directory.CreateDirectory(Path.Combine(zed, ".zed"));
            Assert.True(WorkspaceIdeDetector.Detect(zed).HasFlag(IdeWorkspaceKind.Zed));

            // 标记在父目录、cwd 在子目录时也要认出来(向上查找)。
            var child = Path.Combine(vscode, "src", "deep");
            Directory.CreateDirectory(child);
            Assert.True(WorkspaceIdeDetector.Detect(child).HasFlag(IdeWorkspaceKind.VsCode));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void Query_IsGatedByWorkspaceMarkers_And_FiltersProviders()
    {
        var root = TempTree.CreateDirectory("workspace-service");
        try
        {
            var workspace = Path.Combine(root, "project");
            Directory.CreateDirectory(Path.Combine(workspace, ".vscode"));
            var target = Path.Combine(workspace, "app.py");
            File.WriteAllText(target, "third version");
            var home = Path.Combine(root, "user-home");
            WriteSyntheticVsCodeStore(home, target, ("v1.txt", 1780000001000, "first version"), ("v2.txt", 1780000002000, "second version"));

            var ctx = new Context();
            var registry = IdeHistoryRegistry.CreateDefault(home);
            var service = new IdeHistoryService(ctx, registry, new IdeHistoryOptions());

            Assert.True(service.WorkspaceKind(workspace).HasFlag(IdeWorkspaceKind.VsCode));
            var entries = service.Query(workspace, from: null, to: null);
            Assert.Equal(2, entries.Count);
            Assert.Equal(1780000002000, entries[0].Timestamp);

            var content = service.Read(entries[1]);
            Assert.NotNull(content);
            Assert.Equal("first version", content.Text);

            // 时间窗: 只要更早的那一条。
            Assert.Single(service.Query(workspace, from: null, to: 1780000001500));

            // 无工作区标记: 完全不查(不做全局扫描)。
            Assert.Empty(service.Query(Path.Combine(root, "no-markers"), from: null, to: null));

            // providers 配置把 vscode 摘掉后, 同一条历史不再返回。
            var jetbrainsOnly = new IdeHistoryService(new Context(), registry, IdeHistoryOptions.From(
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["providers"] = new object?[] { "jetbrains" } }));
            Assert.Empty(jetbrainsOnly.Query(workspace, from: null, to: null));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void Options_Parse_Providers_And_TimelineLimit()
    {
        var defaults = IdeHistoryOptions.From(null);
        Assert.Equal(IdeHistoryOptions.DefaultTimelineLimit, defaults.TimelineLimit);
        Assert.Equal(["vscode", "jetbrains", "zed"], defaults.Providers);

        var configured = IdeHistoryOptions.From(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["providers"] = new object?[] { "vscode" },
            ["timeline_limit"] = 7L,
            ["timeline"] = true,
        });
        Assert.Equal(["vscode"], configured.Providers);
        Assert.Equal(7, configured.TimelineLimit);
        Assert.True(configured.Timeline);

        // 时间线默认关(IDE 噪声), 只在显式 timeline: true 时才并进 /timestamp。
        Assert.False(defaults.Timeline);
        Assert.False(IdeHistoryOptions.From(new Dictionary<string, object?>(StringComparer.Ordinal) { ["timeline"] = false }).Timeline);
        Assert.False(new IdeHistoryService(new Context(), IdeHistoryRegistry.CreateDefault(), defaults).TimelineEnabled);

        // 上限被夹到合理区间。
        Assert.Equal(500, IdeHistoryOptions.From(new Dictionary<string, object?>(StringComparer.Ordinal) { ["timeline_limit"] = 9999L }).TimelineLimit);
    }

    /** VS Code 的 History 布局: User/History/<id>/{entries.json,vN.txt}(与 IdeHistoryTests 的合成样本同构)。 */
    private static void WriteSyntheticVsCodeStore(string home, string resourcePath, params (string Id, long Timestamp, string Text)[] versions)
    {
        var userDir = Path.Combine(home, ".config", "Code", "User");
        var historyDir = Path.Combine(userDir, "History", "abc123");
        Directory.CreateDirectory(historyDir);
        var entries = versions
            .Select(version =>
            {
                File.WriteAllText(Path.Combine(historyDir, version.Id), version.Text);
                return $$"""{ "id": "{{version.Id}}", "timestamp": {{version.Timestamp}}, "source": "User" }""";
            });
        var resource = new Uri(Path.GetFullPath(resourcePath)).AbsoluteUri;
        File.WriteAllText(
            Path.Combine(historyDir, "entries.json"),
            $$"""
            {
              "version": 1,
              "resource": "{{resource}}",
              "entries": [{{string.Join(',', entries)}}]
            }
            """);
    }
}
