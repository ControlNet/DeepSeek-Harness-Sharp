using Dsh.Ide.History;
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

    [Fact]
    public void Options_NestedValuesOverrideFlatAliases_AndEmptyProvidersDisableHistory()
    {
        var options = IdeHistoryOptions.From(new Dictionary<string, object?>
        {
            ["timeline"] = true,
            ["timelineLimit"] = 8,
            ["providers"] = new object?[] { "vscode" },
            ["history"] = new Dictionary<string, object?>
            {
                ["timeline"] = false,
                ["providers"] = Array.Empty<object?>(),
                ["window_seconds"] = 12,
            },
        });

        Assert.False(options.Timeline);
        Assert.Empty(options.Providers);
        Assert.Equal(8, options.TimelineLimit);
        Assert.Equal(12, options.WindowSeconds);
    }

    [Fact]
    public void Timeline_DeduplicatesContent_GroupsTimeWindows_AndMarksActualGitHead()
    {
        using var repo = new GitTestRepository();
        repo.Commit(("a.txt", "head\n"), ("b.txt", "second\n"));
        Directory.CreateDirectory(Path.Combine(repo.Root, ".vscode"));
        var home = Path.Combine(repo.Sandbox, "editor-data");
        var first = Path.Combine(repo.Root, "a.txt");
        var second = Path.Combine(repo.Root, "b.txt");
        const long start = 1780000000000;
        WriteSyntheticVsCodeStore(home, first,
            ("old", start, "old\n"), ("same-1", start + 120000, "head\n"), ("same-2", start + 121000, "head\n"));
        WriteSyntheticVsCodeStore(home, second, ("second", start + 121500, "second\n"));
        var service = new IdeHistoryService(new Context(), IdeHistoryRegistry.CreateDefault(home, Path.Combine(home, "no-jb")),
            new IdeHistoryOptions { Timeline = true });

        var moments = service.QueryTimeline(repo.Root);

        Assert.Equal(2, moments.Count);
        Assert.Equal(2, moments[0].Entries.Count);
        Assert.All(moments[0].Entries, entry => Assert.True(entry.MatchesGitHead));
        var old = Assert.Single(moments[1].Entries);
        Assert.False(old.MatchesGitHead);
        Assert.Equal("old\n", service.Read(old)!.Text);
        Assert.Equal(start + 121000, Assert.Single(moments[0].Entries, entry => entry.Path == first).Timestamp);
    }

    [Theory]
    [InlineData("a.txt", "a.txt ")]
    [InlineData(@"literal\name.txt", "literal/name.txt")]
    public void Timeline_PreservesDistinctNativeUnixPaths_WithIdenticalContent(string firstName, string secondName)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Trailing spaces and literal backslashes are distinct filename characters on Unix.");
        using var repo = new GitTestRepository();
        repo.Commit((firstName, "same\n"), (secondName, "same\n"));
        Directory.CreateDirectory(Path.Combine(repo.Root, ".vscode"));
        var home = Path.Combine(repo.Sandbox, "editor-data");
        var first = Path.Combine(repo.Root, firstName);
        var second = Path.Combine(repo.Root, secondName);
        WriteSyntheticVsCodeStore(home, first, ("first", 1780000000000, "same\n"));
        WriteSyntheticVsCodeStore(home, second, ("second", 1780000001000, "same\n"));
        var service = new IdeHistoryService(new Context(), IdeHistoryRegistry.CreateDefault(home, Path.Combine(home, "no-jb")),
            new IdeHistoryOptions { Timeline = true });

        var moment = Assert.Single(service.QueryTimeline(repo.Root));

        Assert.Equal(2, moment.Entries.Count);
        Assert.Single(moment.Entries, entry => entry.Path == first);
        Assert.Single(moment.Entries, entry => entry.Path == second);
        Assert.All(moment.Entries, entry => Assert.Equal("same\n", service.Read(entry)!.Text));
    }

    [Fact]
    public void Timeline_LiteralBackslashFilename_MatchesItsActualGitHeadOnUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("A backslash is a filename character only on Unix.");
        using var repo = new GitTestRepository();
        const string name = @"literal\name.txt";
        repo.Commit((name, "literal head\n"), ("literal/name.txt", "directory head\n"));
        Directory.CreateDirectory(Path.Combine(repo.Root, ".vscode"));
        var home = Path.Combine(repo.Sandbox, "editor-data");
        var target = Path.Combine(repo.Root, name);
        WriteSyntheticVsCodeStore(home, target,
            ("old", 1780000000000, "directory head\n"), ("head", 1780000001000, "literal head\n"));
        var service = new IdeHistoryService(new Context(), IdeHistoryRegistry.CreateDefault(home, Path.Combine(home, "no-jb")),
            new IdeHistoryOptions { Timeline = true });

        var moment = Assert.Single(service.QueryTimeline(repo.Root));

        Assert.Equal(2, moment.Entries.Count);
        Assert.All(moment.Entries, entry => Assert.Equal(target, entry.Path));
        Assert.True(Assert.Single(moment.Entries, entry => entry.Timestamp == 1780000001000).MatchesGitHead);
        Assert.False(Assert.Single(moment.Entries, entry => entry.Timestamp == 1780000000000).MatchesGitHead);
    }

    [Fact]
    public void Query_TimeBoundsApplyBeforeTheLimit_AndContentRemainsLazy()
    {
        var root = TempTree.CreateDirectory("history-bounds");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".vscode"));
            var target = Path.Combine(root, "a.txt");
            var home = Path.Combine(root, "editor-data");
            WriteSyntheticVsCodeStore(home, target, ("old", 1000, "old"), ("new", 2000, "new"));
            var service = new IdeHistoryService(new Context(), IdeHistoryRegistry.CreateDefault(home, Path.Combine(root, "no-jb")),
                new IdeHistoryOptions { TimelineLimit = 1 });

            var entry = Assert.Single(service.Query(root, 1000, 1000));
            Assert.Equal(1000, entry.Timestamp);
            File.WriteAllText(entry.EntryFile!, "changed after metadata query");
            Assert.Equal("changed after metadata query", service.Read(entry)!.Text);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void Restore_Previews_RequiresConfirmation_BacksUpExactBytes_AndRejectsStalePreview()
    {
        var root = TempTree.CreateDirectory("history-restore");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".vscode"));
            var target = Path.Combine(root, "file.bin");
            var current = new byte[] { 0, 255, 1, 2 };
            File.WriteAllBytes(target, current);
            var home = Path.Combine(root, "editor-data");
            WriteSyntheticVsCodeStore(home, target, ("old", 1000, "revision"));
            var ctx = new Context();
            ctx.SetOwn("dshHomePath", Path.Combine(root, "dsh-data"));
            var service = new IdeHistoryService(ctx, IdeHistoryRegistry.CreateDefault(home, Path.Combine(root, "no-jb")), new());
            var entry = Assert.Single(service.Query(root, null, null));
            var preview = service.PreviewRestore(root, entry);

            Assert.Contains("Binary revision", preview.Diff);
            Assert.True(preview.ModifiedSinceEntry);
            Assert.Equal(current, File.ReadAllBytes(target));
            Assert.False(service.Restore(preview, confirmModified: false).Restored);
            Assert.False(Directory.Exists(Path.Combine(root, "dsh-data", "checkpoints", "ide-backups")));

            File.WriteAllText(target, "later edit");
            Assert.False(service.Restore(preview, confirmModified: true).Restored);
            Assert.Equal("later edit", File.ReadAllText(target));
            var fresh = service.PreviewRestore(root, entry);
            var result = service.Restore(fresh, confirmModified: true);
            Assert.True(result.Restored);
            Assert.Equal("revision", File.ReadAllText(target));
            Assert.Equal("later edit", File.ReadAllText(result.BackupPath!));
            Assert.True(File.Exists($"{result.BackupPath}.json"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(result.BackupPath!));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void Restore_RejectsTargetsOutsideTheRealWorkspace()
    {
        var root = TempTree.CreateDirectory("history-containment");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(outside);
            var target = Path.Combine(outside, "file.txt");
            File.WriteAllText(target, "untouched");
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), outside);
                target = Path.Combine(workspace, "link", "file.txt");
            }
            var entry = new IdeHistoryEntry { Provider = "vscode", Path = target, Timestamp = 1000, Kind = "content" };
            Assert.Throws<InvalidOperationException>(() => HistoryRestore.Preview(workspace, entry,
                new IdeHistoryContent { Bytes = "replacement"u8.ToArray(), Origin = "fixture" }));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, "file.txt")));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void Restore_MissingTargetRequiresConfirmationAndRecordsTheOriginalAbsence()
    {
        var root = TempTree.CreateDirectory("history-missing");
        try
        {
            var target = Path.Combine(root, "missing.txt");
            var entry = new IdeHistoryEntry { Provider = "vscode", Path = target, Timestamp = 1000, Kind = "content" };
            var preview = HistoryRestore.Preview(root, entry, new IdeHistoryContent { Bytes = "restored"u8.ToArray(), Origin = "fixture" });
            var backups = Path.Combine(root, "backups");

            Assert.False(preview.TargetExists);
            Assert.False(HistoryRestore.Restore(preview, backups, confirmModified: false).Restored);
            Assert.False(File.Exists(target));
            var result = HistoryRestore.Restore(preview, backups, confirmModified: true);
            Assert.True(result.Restored);
            Assert.Equal("restored", File.ReadAllText(target));
            Assert.Empty(File.ReadAllBytes(result.BackupPath!));
            var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText($"{result.BackupPath}.json"));
            Assert.False(metadata!["existed"]!.GetValue<bool>());
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void Restore_DoesNotOverwriteAnExclusivelyHeldFile()
    {
        var root = TempTree.CreateDirectory("history-held");
        try
        {
            var target = Path.Combine(root, "held.txt");
            File.WriteAllText(target, "current");
            var entry = new IdeHistoryEntry { Provider = "vscode", Path = target, Timestamp = 1000, Kind = "content" };
            var preview = HistoryRestore.Preview(root, entry, new IdeHistoryContent { Bytes = "revision"u8.ToArray(), Origin = "fixture" });

            using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.False(HistoryRestore.Restore(preview, Path.Combine(root, "backups"), confirmModified: true).Restored);
            Assert.Equal("current", File.ReadAllText(target));
            Assert.False(Directory.Exists(Path.Combine(root, "backups")));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** VS Code 的 History 布局: User/History/<id>/{entries.json,vN.txt}(与 IdeHistoryTests 的合成样本同构)。 */
    private static void WriteSyntheticVsCodeStore(string home, string resourcePath, params (string Id, long Timestamp, string Text)[] versions)
    {
        var userDir = Path.Combine(home, ".config", "Code", "User");
        var historyDir = Path.Combine(userDir, "History", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(historyDir);
        var entries = versions
            .Select(version =>
            {
                File.WriteAllText(Path.Combine(historyDir, version.Id), version.Text);
                return $$"""{ "id": "{{version.Id}}", "timestamp": {{version.Timestamp}}, "source": "User" }""";
            });
        var fullPath = Path.GetFullPath(resourcePath);
        // File URI segments retain native Unix filename characters.
        var resource = OperatingSystem.IsWindows()
            ? new Uri(fullPath).AbsoluteUri
            : $"file://{string.Join('/', fullPath.Split('/').Select(Uri.EscapeDataString))}";
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
