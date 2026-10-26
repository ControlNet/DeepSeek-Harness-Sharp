using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.Tools;
using Dsh.Plugins;
using Dsh.Ide.History;

namespace Dsh.Tests;

public class ConfigBootTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public async Task Compose_IdeLegacyAliasHonorsCanonicalPrecedenceWithoutDoubleActivation(bool legacy, bool? canonical, bool enabled)
    {
        var root = TempTree.CreateDirectory("ide-alias");
        try
        {
            var home = HarnessHome.Resolve(root);
            var extra = canonical is null ? "" : $"\n  \"@deepseek-ai/dsh-ide\": {canonical.Value.ToString().ToLowerInvariant()}";
            File.WriteAllText(home.SettingsFile, $"plugins:\n  \"@deepseek-ai/dsh-ide-history\": {legacy.ToString().ToLowerInvariant()}{extra}\n");
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: root));

            var activation = app.Composition!.Find(PluginPackageNames.Ide);
            Assert.Equal(enabled, activation is { State: ActivationState.Active });
            Assert.Null(app.Composition.Find(PluginPackageNames.LegacyIdeHistory));
            Assert.Equal(enabled, app.Ctx.Get<IIdeHistoryService>(IIdeHistoryService.ServiceName, false) is not null);
            var catalog = Assert.IsType<PluginCatalog>(app.Ctx.GetProp("pluginCatalog"));
            Assert.Contains(PluginPackageNames.Ide, catalog.PackageNames);
            Assert.DoesNotContain(PluginPackageNames.LegacyIdeHistory, catalog.PackageNames);
            Assert.Equal(PluginPackageNames.Ide, catalog.CreateDefinition(PluginPackageNames.LegacyIdeHistory).Name);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public async Task Compose_LegacyHistoryParametersAndCommentsSurvivePluginToggle()
    {
        var root = TempTree.CreateDirectory("ide-alias-toggle");
        try
        {
            var home = HarnessHome.Resolve(root);
            File.WriteAllText(home.SettingsFile, """
                plugins:
                  "@deepseek-ai/dsh-ide-history": # legacy package comment
                    timeline: true # legacy timeline comment
                    future_key: keep
                """);
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: root));
            var manager = app.Ctx.Get<IPluginManager>("pluginManager", false)!;
            Assert.True(app.Ctx.Get<IIdeHistoryService>(IIdeHistoryService.ServiceName, false)!.TimelineEnabled);

            await manager.DisableAsync(PluginPackageNames.LegacyIdeHistory);
            await manager.EnableAsync(PluginPackageNames.LegacyIdeHistory);

            Assert.True(app.Ctx.Get<IIdeHistoryService>(IIdeHistoryService.ServiceName, false)!.TimelineEnabled);
            var saved = HarnessSettings.Load(home);
            Assert.DoesNotContain(PluginPackageNames.Ide, saved.Plugins.Keys);
            Assert.Equal("keep", saved.Plugins[PluginPackageNames.LegacyIdeHistory].Parameters["future_key"]);
            var text = File.ReadAllText(home.SettingsFile);
            Assert.Contains("# legacy package comment", text);
            Assert.Contains("# legacy timeline comment", text);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public async Task Compose_ActivatesConfiguredPluginsAndMergesDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-configboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            Directory.CreateDirectory(home.Root);
            await File.WriteAllTextAsync(Path.Combine(home.Root, "settings.yaml"), """
                global_default_model: deepseek-official/deepseek-v4-flash
                plugins:
                  "@deepseek-ai/dsh-core": true
                  "@deepseek-ai/dsh-interaction": true
                  "@deepseek-ai/dsh-persistence": true
                  "@deepseek-ai/dsh-subprocess": true
                  "@deepseek-ai/dsh-tool-bash": true
                  "@deepseek-ai/dsh-tool-fs": true
                  "@deepseek-ai/dsh-tool-fs-search":
                    sampleOverCapGlobResults: false
                  "@deepseek-ai/dsh-tool-todo":
                    allowParallelInProgress: true
                  "@deepseek-ai/dsh-fs-local": true
                  "@deepseek-ai/dsh-tool-str-replace-editor":
                    maxOutputChars: 16000
                """, TestContext.Current.CancellationToken);

            app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));

            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            var missing = new[] { "bash", "read", "write", "edit", "glob", "grep", "todo_write", "str_replace_editor" }
                .Where(name => tools.Get(name) is null)
                .ToList();
            Assert.True(missing.Count == 0, "missing tools: " + string.Join(", ", missing));

            Assert.NotNull(app.Ctx.Get<LocalFsService>(LocalFsService.ServiceName));

            // 用户 settings.yaml 未列出的插件发现即启用
            Assert.Equal(ActivationState.Active, app.Composition!.Find("@deepseek-ai/dsh-checkpoints")?.State);
            Assert.Equal(ActivationState.Active, app.Composition.Find("@deepseek-ai/dsh-ide")?.State);

            var fsSearch = app.Composition.Find("@deepseek-ai/dsh-tool-fs-search")!;
            Assert.Equal(false, (fsSearch.Config as IDictionary<string, object?>)?["sampleOverCapGlobResults"]);
        }
        finally
        {
            app?.Dispose();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Compose_SkipsDisabledPlugins()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-configboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            Directory.CreateDirectory(home.Root);
            await File.WriteAllTextAsync(Path.Combine(home.Root, "settings.yaml"), """
                plugins:
                  "@deepseek-ai/dsh-tool-todo": false
                """, TestContext.Current.CancellationToken);

            app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));

            Assert.Null(app.Composition!.Find("@deepseek-ai/dsh-tool-todo"));
            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            Assert.Null(tools.Get("todo_write"));
            Assert.NotNull(tools.Get("bash"));
        }
        finally
        {
            app?.Dispose();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Compose_StorageRootRedirectsHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-configboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            Directory.CreateDirectory(home.Root);
            var target = Path.Combine(dir, "data");
            await File.WriteAllTextAsync(Path.Combine(home.Root, "settings.yaml"), $"""
                global_default_model: deepseek-official/deepseek-v4-flash
                storage:
                  root: {target.Replace('\\', '/')}
                plugins:
                  "@deepseek-ai/dsh-core": true
                """, TestContext.Current.CancellationToken);

            app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));

            Assert.Equal(Path.GetFullPath(target), app.Home.Root);
            Assert.Equal(Path.GetFullPath(target), (string)app.Ctx.GetProp("dshHomePath")!);
            Assert.True(File.Exists(Path.Combine(target, "settings.yaml")));
        }
        finally
        {
            app?.Dispose();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
